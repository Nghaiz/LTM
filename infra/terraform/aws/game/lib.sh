# Shared helpers for infra/terraform/aws/game/deploy.sh. Source it; do not run it.
#
# Reads the instance id and region from `terraform output` in infra/terraform/aws/game/, and runs commands
# on the instance through SSM Run Command — no SSH key, no open port 22.

AWS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

die() { echo "[$(basename "$0")] ERROR: $*" >&2; exit 1; }
log() { echo "==> $*"; }

command -v aws >/dev/null 2>&1 || die "aws CLI not on PATH"
command -v jq >/dev/null 2>&1 || die "jq not on PATH"

tf_out() {
    command -v terraform >/dev/null 2>&1 || die "terraform not on PATH; set INSTANCE_ID and REGION instead"
    terraform -chdir="$AWS_DIR" output -raw "$1"
}

INSTANCE_ID="${INSTANCE_ID:-$(tf_out instance_id)}"
REGION="${REGION:-$(tf_out region)}"
[ -n "$INSTANCE_ID" ] || die "no instance_id — has infra/terraform/aws/game been applied?"

ssm_run() {
    local cmd="$1" cid status
    cid=$(aws ssm send-command --region "$REGION" --instance-ids "$INSTANCE_ID" \
        --document-name AWS-RunShellScript --comment "ironfront-game: $(basename "$0")" \
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
