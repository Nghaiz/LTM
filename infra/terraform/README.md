# Terraform

One root module per platform, split by role where a platform runs more than one:

| Directory | What | Status |
|---|---|---|
| [`aws/master/`](aws/master/README.md) | The master server on one EC2 instance (replaces fly.io) | **live** |
| [`aws/game/`](aws/game/README.md) | The game servers on one EC2 instance, beside the master | **standby**: players use the Azure VM |
| [`azure/`](azure/README.md) | Phase-03 single VM for master + game servers (Korea Central, issue #78) | history: that VM is gone |

The players' game servers run on the Azure VM `ironfront-game`, which is not managed by Terraform
(see `infra/docker/gameservers.compose.yml`; deploy with CD target `gameservers` or
`tools/deploy-gameservers-azure.ps1`). `aws/game/` is the prepared replacement for when the Azure
credit runs out (owner, 2026-11-10); CD target `gameservers-aws` deploys to it.

Anything shared between modules lives here at the top level. Today that is only `.gitignore`,
which keeps state, `terraform.tfvars` and plans out of git for every module below it.
