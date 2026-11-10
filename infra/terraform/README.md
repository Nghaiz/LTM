# Terraform

One root module per platform, split by role where a platform runs more than one:

| Directory | What | Status |
|---|---|---|
| [`aws/master/`](aws/master/README.md) | The master server on one EC2 instance (replaces fly.io) | **live** |
| [`aws/game/`](aws/game/README.md) | The game servers on one EC2 instance (Dustbowl, Island, Forest Lake) | **live** |
| [`azure/`](azure/README.md) | Phase-03 single VM for master + game servers (Korea Central, issue #78) | history: that VM is gone |

The game servers run on the AWS EC2 instance managed by `aws/game/` in the same VPC as `aws/master/`.

Anything shared between modules lives here at the top level. Today that is only `.gitignore`,
which keeps state, `terraform.tfvars` and plans out of git for every module below it.
