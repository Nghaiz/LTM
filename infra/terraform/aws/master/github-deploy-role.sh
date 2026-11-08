#!/usr/bin/env bash
# The AWS side of .github/workflows/cd.yml target=master: the identity GitHub Actions deploys the
# master with, allowed only to run SSM commands on the master instance.
#
#   INSTANCE_ID=i-... REGION=ap-southeast-2 BUCKET=- ./infra/terraform/aws/master/github-deploy-role.sh [mode] [--apply]
#   (without INSTANCE_ID/REGION/BUCKET, lib.sh reads them from `terraform output`)
#
# Modes (dry run unless --apply):
#   --oidc  (default) IAM role assumed through GitHub OIDC; no stored AWS key. Records
#           AWS_DEPLOY_ROLE_ARN in the GitHub environment.
#   --user  IAM user with an access key, for accounts whose organization SCP denies the
#           iam:*OpenIDConnectProvider calls (account 648879824333 does, measured 2026-11-09).
#           Stores AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY as environment SECRETS straight from
#           the CLI output (never printed). An existing key is kept; --rotate-key replaces it.
# Both record MASTER_INSTANCE_ID in the environment.
#
# Idempotent: policies are rewritten every run to exactly what is below.
# Why a script and not main.tf: this directory's terraform state is not on the machines that
# deploy, and an apply without the state would plan to create the whole stack again.
#
# Scope (both modes): ssm:SendCommand with AWS-RunShellScript on this one instance — root on the
# master, which is what deploying it means — plus reading the command's result. The GitHub
# environment (main only, owner approval on every run) is what guards the credentials.
#
# Needs: aws CLI signed in as an IAM admin (`aws login`), jq, gh (repo admin) for --apply.

set -euo pipefail
# shellcheck source=lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

NAME="${NAME:-ironfront-github-deploy}"
GITHUB_REPO="${GITHUB_REPO:-Nghaiz/LTM}"
GITHUB_ENVIRONMENT="${GITHUB_ENVIRONMENT:-production}"
OIDC_HOST=token.actions.githubusercontent.com
mode=oidc apply=0 rotate=0
for arg in "$@"; do
    case "$arg" in
        --oidc) mode=oidc ;;
        --user) mode=user ;;
        --apply) apply=1 ;;
        --rotate-key) rotate=1 ;;
        *) die "unknown argument: $arg" ;;
    esac
done

account=$(aws sts get-caller-identity --query Account --output text) || die "aws CLI is not signed in (aws login)"
aws ec2 describe-instances --region "$REGION" --instance-ids "$INSTANCE_ID" \
    --query 'Reservations[0].Instances[0].InstanceId' --output text >/dev/null 2>&1 \
    || die "instance $INSTANCE_ID not found in account $account, region $REGION"

permissions=$(jq -n --arg r "$REGION" --arg a "$account" --arg i "$INSTANCE_ID" '{
  Version: "2012-10-17",
  Statement: [
    {Sid: "RunOnTheMasterOnly", Effect: "Allow", Action: "ssm:SendCommand",
     Resource: ["arn:aws:ec2:\($r):\($a):instance/\($i)", "arn:aws:ssm:\($r)::document/AWS-RunShellScript"]},
    {Sid: "ReadCommandResults", Effect: "Allow", Action: "ssm:GetCommandInvocation", Resource: "*"}
  ]}')

log "account $account, region $REGION, instance $INSTANCE_ID, mode $mode, name $NAME"
echo "$permissions" | jq -c .

gh_env() { gh "$@" --repo "$GITHUB_REPO" --env "$GITHUB_ENVIRONMENT"; }

if [ "$mode" = oidc ]; then
    provider_arn="arn:aws:iam::$account:oidc-provider/$OIDC_HOST"
    trust=$(jq -n --arg p "$provider_arn" --arg h "$OIDC_HOST" \
        --arg sub "repo:$GITHUB_REPO:environment:$GITHUB_ENVIRONMENT" '{
      Version: "2012-10-17",
      Statement: [{
        Effect: "Allow",
        Principal: {Federated: $p},
        Action: "sts:AssumeRoleWithWebIdentity",
        Condition: {StringEquals: {($h + ":aud"): "sts.amazonaws.com", ($h + ":sub"): $sub}}
      }]}')
    echo "$trust" | jq -c .
    [ "$apply" = 1 ] || { log "dry run; pass --apply to create or update"; exit 0; }

    if aws iam get-open-id-connect-provider --open-id-connect-provider-arn "$provider_arn" >/dev/null 2>&1; then
        log "OIDC provider exists: $provider_arn"
    else
        aws iam create-open-id-connect-provider --url "https://$OIDC_HOST" --client-id-list sts.amazonaws.com >/dev/null \
            || die "cannot create the OIDC provider (an organization SCP may deny it); use --user"
        log "created OIDC provider $provider_arn"
    fi
    if aws iam get-role --role-name "$NAME" >/dev/null 2>&1; then
        aws iam update-assume-role-policy --role-name "$NAME" --policy-document "$trust"
        log "updated the trust policy of role $NAME"
    else
        aws iam create-role --role-name "$NAME" --assume-role-policy-document "$trust" \
            --description "GitHub Actions CD (repo:$GITHUB_REPO:environment:$GITHUB_ENVIRONMENT): SSM commands on the Ironfront master" \
            --max-session-duration 3600 >/dev/null
        log "created role $NAME"
    fi
    aws iam put-role-policy --role-name "$NAME" --policy-name ssm-deploy-master --policy-document "$permissions"
    role_arn=$(aws iam get-role --role-name "$NAME" --query Role.Arn --output text)
    gh_env variable set AWS_DEPLOY_ROLE_ARN --body "$role_arn"
    log "role ready and recorded: $role_arn"
else
    [ "$apply" = 1 ] || { log "dry run; pass --apply to create or update"; exit 0; }

    if aws iam get-user --user-name "$NAME" >/dev/null 2>&1; then
        log "user $NAME exists"
    else
        aws iam create-user --user-name "$NAME" \
            --tags "Key=purpose,Value=GitHub Actions CD $GITHUB_REPO environment $GITHUB_ENVIRONMENT" >/dev/null
        log "created user $NAME"
    fi
    aws iam put-user-policy --user-name "$NAME" --policy-name ssm-deploy-master --policy-document "$permissions"
    log "policy ssm-deploy-master set on user $NAME"

    keys=$(aws iam list-access-keys --user-name "$NAME" --query 'AccessKeyMetadata[].AccessKeyId' --output text)
    if [ -n "$keys" ] && [ "$rotate" = 0 ]; then
        log "user already has access key(s) $keys; kept (pass --rotate-key to replace and re-store)"
    else
        # The secret goes from the CLI's JSON straight into gh's stdin; it is never echoed or written.
        created=$(aws iam create-access-key --user-name "$NAME" --output json)
        new_id=$(jq -r .AccessKey.AccessKeyId <<<"$created")
        jq -r .AccessKey.SecretAccessKey <<<"$created" | gh_env secret set AWS_SECRET_ACCESS_KEY
        printf '%s' "$new_id" | gh_env secret set AWS_ACCESS_KEY_ID
        unset created
        log "created access key $new_id and stored it in the $GITHUB_ENVIRONMENT environment"
        for old in $keys; do
            aws iam delete-access-key --user-name "$NAME" --access-key-id "$old"
            log "deleted the previous access key $old"
        done
    fi
fi

gh_env variable set MASTER_INSTANCE_ID --body "$INSTANCE_ID"
log "recorded MASTER_INSTANCE_ID=$INSTANCE_ID in the $GITHUB_ENVIRONMENT environment"
