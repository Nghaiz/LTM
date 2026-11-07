# Shared helpers for infra/terraform/aws/master/deploy.sh. Source it; do not run it.
#
# Reads the instance id, region and bucket from `terraform output` in infra/terraform/aws/master/, and runs commands
# on the instance through SSM Run Command — no SSH key, no open port 22.

AWS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

die() { echo "[$(basename "$0")] ERROR: $*" >&2; exit 1; }
log() { echo "==> $*"; }

command -v aws >/dev/null 2>&1 || die "aws CLI not on PATH"
command -v jq >/dev/null 2>&1 || die "jq not on PATH"
command -v terraform >/dev/null 2>&1 || die "terraform not on PATH"

tf_out() { terraform -chdir="$AWS_DIR" output -raw "$1"; }

INSTANCE_ID="${INSTANCE_ID:-$(tf_out instance_id)}"
REGION="${REGION:-$(tf_out region)}"
BUCKET="${BUCKET:-$(tf_out backup_bucket)}"
[ -n "$INSTANCE_ID" ] || die "no instance_id — has infra/terraform/aws/master been applied?"

# ssm_run "<shell command>": runs it as root on the instance, prints its output, fails if it did.
ssm_run() {
    local cmd="$1" cid status
    cid=$(aws ssm send-command --region "$REGION" --instance-ids "$INSTANCE_ID" \
        --document-name AWS-RunShellScript --comment "ironfront: $(basename "$0")" \
        --parameters "$(jq -nc --arg c "$cmd" '{commands: [$c], executionTimeout: ["1800"]}')" \
        --query Command.CommandId --output text) || die "send-command failed"
    while :; do
        sleep 3
        status=$(aws ssm get-command-invocation --region "$REGION" --command-id "$cid" \
            --instance-id "$INSTANCE_ID" --query Status --output text 2>/dev/null || echo Pending)
        case "$status" in Pending|InProgress|Delayed) continue ;; *) break ;; esac
    done
    aws ssm get-command-invocation --region "$REGION" --command-id "$cid" --instance-id "$INSTANCE_ID" \
        --query '[StandardOutputContent, StandardErrorContent]' --output text
    [ "$status" = "Success" ] || die "remote command ended $status"
}
