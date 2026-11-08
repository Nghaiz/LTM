#!/usr/bin/env bash
# The AWS side of .github/workflows/cd.yml target=master: an IAM role that GitHub Actions assumes
# through OIDC (no stored AWS key), allowed only to run SSM commands on the master instance.
#
#   INSTANCE_ID=i-... REGION=ap-southeast-2 BUCKET=- ./infra/terraform/aws/master/github-deploy-role.sh   # dry run
#   (without INSTANCE_ID/REGION/BUCKET, lib.sh reads them from `terraform output`)
#   ./infra/terraform/aws/master/github-deploy-role.sh            # print what it would do
#   ./infra/terraform/aws/master/github-deploy-role.sh --apply    # create or update, then record
#                                                                  # the role in GitHub
#
# Idempotent: the OIDC provider is created only if absent; the role's trust and permission
# policies are rewritten every run to exactly what is below.
#
# Why a script and not main.tf: this directory's terraform state is not on the machines that
# deploy, and an apply without the state would plan to create the whole stack again.
#
# Trust: only tokens for repo:Nghaiz/LTM:environment:production. That environment admits only
# main and requires the owner's approval, so the role is reachable only through an approved run
# from main. Permission: ssm:SendCommand with AWS-RunShellScript on this one instance (root on the
# master: that is what deploying it means), plus reading the command's result.
#
# Needs: aws CLI signed in as an IAM admin (`aws login`), jq, gh (repo admin) for --apply.

set -euo pipefail
# shellcheck source=lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

ROLE_NAME="${ROLE_NAME:-ironfront-github-deploy}"
GITHUB_REPO="${GITHUB_REPO:-Nghaiz/LTM}"
GITHUB_ENVIRONMENT="${GITHUB_ENVIRONMENT:-production}"
OIDC_HOST=token.actions.githubusercontent.com
apply=0
[ "${1:-}" = "--apply" ] && apply=1

account=$(aws sts get-caller-identity --query Account --output text) || die "aws CLI is not signed in (aws login)"
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

permissions=$(jq -n --arg r "$REGION" --arg a "$account" --arg i "$INSTANCE_ID" '{
  Version: "2012-10-17",
  Statement: [
    {Sid: "RunOnTheMasterOnly", Effect: "Allow", Action: "ssm:SendCommand",
     Resource: ["arn:aws:ec2:\($r):\($a):instance/\($i)", "arn:aws:ssm:\($r)::document/AWS-RunShellScript"]},
    {Sid: "ReadCommandResults", Effect: "Allow", Action: "ssm:GetCommandInvocation", Resource: "*"}
  ]}')

log "account $account, region $REGION, instance $INSTANCE_ID"
log "role $ROLE_NAME, trusted for repo:$GITHUB_REPO:environment:$GITHUB_ENVIRONMENT"
echo "$trust" | jq -c .
echo "$permissions" | jq -c .
[ "$apply" = 1 ] || { log "dry run; pass --apply to create or update"; exit 0; }

if aws iam get-open-id-connect-provider --open-id-connect-provider-arn "$provider_arn" >/dev/null 2>&1; then
    log "OIDC provider exists: $provider_arn"
else
    aws iam create-open-id-connect-provider --url "https://$OIDC_HOST" --client-id-list sts.amazonaws.com >/dev/null
    log "created OIDC provider $provider_arn"
fi

if aws iam get-role --role-name "$ROLE_NAME" >/dev/null 2>&1; then
    aws iam update-assume-role-policy --role-name "$ROLE_NAME" --policy-document "$trust"
    log "updated the trust policy of $ROLE_NAME"
else
    aws iam create-role --role-name "$ROLE_NAME" --assume-role-policy-document "$trust" \
        --description "GitHub Actions CD (repo:$GITHUB_REPO:environment:$GITHUB_ENVIRONMENT): SSM commands on the Ironfront master" \
        --max-session-duration 3600 >/dev/null
    log "created role $ROLE_NAME"
fi
aws iam put-role-policy --role-name "$ROLE_NAME" --policy-name ssm-deploy-master --policy-document "$permissions"
role_arn=$(aws iam get-role --role-name "$ROLE_NAME" --query Role.Arn --output text)
log "role ready: $role_arn"

gh variable set AWS_DEPLOY_ROLE_ARN --repo "$GITHUB_REPO" --env "$GITHUB_ENVIRONMENT" --body "$role_arn"
gh variable set MASTER_INSTANCE_ID --repo "$GITHUB_REPO" --env "$GITHUB_ENVIRONMENT" --body "$INSTANCE_ID"
log "recorded AWS_DEPLOY_ROLE_ARN and MASTER_INSTANCE_ID in the $GITHUB_ENVIRONMENT environment"
