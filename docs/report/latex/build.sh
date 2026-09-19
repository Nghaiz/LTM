#!/usr/bin/env bash
# Biên dịch cả 4 báo cáo (hoặc các tệp truyền vào) và chuyển PDF ra docs/report/.
#
# Trình biên dịch được chọn theo thứ tự:
#   1. tectonic đã cài sẵn trên máy
#   2. xelatex (TeX Live / MiKTeX) đã cài sẵn
#   3. tectonic cục bộ trong .tools/ -- tự tải lần đầu (~20 MB, không cần sudo)
#
#   ./build.sh                                        # cả 4 bản
#   ./build.sh BaoCao_D_NguyenTuKien_MasterServer.tex # một bản
set -euo pipefail
cd "$(dirname "$0")"

TECTONIC_VERSION="0.17.0"
TOOLS_DIR="$PWD/.tools"

# Bản musl của Tectonic cần biết nơi chứa chứng chỉ CA để tải gói TeX lần đầu.
for ca in /etc/ssl/certs/ca-certificates.crt /etc/pki/tls/certs/ca-bundle.crt /etc/ssl/cert.pem; do
  if [[ -z "${SSL_CERT_FILE:-}" && -f "$ca" ]]; then export SSL_CERT_FILE="$ca"; fi
done

install_local_tectonic() {
  local os arch target
  os="$(uname -s)"; arch="$(uname -m)"
  case "$os-$arch" in
    Linux-x86_64)            target="x86_64-unknown-linux-musl" ;;
    Linux-aarch64)           target="aarch64-unknown-linux-musl" ;;
    Darwin-x86_64)           target="x86_64-apple-darwin" ;;
    Darwin-arm64)            target="aarch64-apple-darwin" ;;
    *) echo "Không hỗ trợ tự tải cho $os-$arch. Hãy cài TeX Live (xelatex) hoặc Tectonic." >&2; exit 1 ;;
  esac
  local url="https://github.com/tectonic-typesetting/tectonic/releases/download/tectonic%40${TECTONIC_VERSION}/tectonic-${TECTONIC_VERSION}-${target}.tar.gz"
  echo "Tải Tectonic ${TECTONIC_VERSION} (${target}) vào .tools/ ..."
  mkdir -p "$TOOLS_DIR"
  curl -fsSL "$url" | tar -xz -C "$TOOLS_DIR"
  chmod +x "$TOOLS_DIR/tectonic"
}

if command -v tectonic >/dev/null 2>&1; then
  ENGINE="tectonic"; TECTONIC="tectonic"
elif command -v xelatex >/dev/null 2>&1; then
  ENGINE="xelatex"
else
  [[ -x "$TOOLS_DIR/tectonic" ]] || install_local_tectonic
  ENGINE="tectonic"; TECTONIC="$TOOLS_DIR/tectonic"
fi
echo "Trình biên dịch: $ENGINE"

files=("$@")
[[ ${#files[@]} -gt 0 ]] || files=(BaoCao_*.tex)

for f in "${files[@]}"; do
  echo "==> $f"
  if [[ "$ENGINE" == "tectonic" ]]; then
    "$TECTONIC" -X compile "$f"
  else
    xelatex -interaction=nonstopmode -halt-on-error "$f" >/dev/null
    xelatex -interaction=nonstopmode -halt-on-error "$f" >/dev/null   # lần 2 cho mục lục và số hình
  fi
  mv "${f%.tex}.pdf" ../
done
rm -f ./*.aux ./*.log ./*.toc ./*.out
echo "Xong: PDF nằm trong docs/report/"
