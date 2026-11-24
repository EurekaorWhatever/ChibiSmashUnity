#!/usr/bin/env sh
# Download the official Noto fonts (SIL Open Font License 1.1) used by localization stage 3 into a FRESH folder.
# Usage: sh tools/fonts/fetch_fonts.sh <empty_download_dir>   then   python -P tools/fonts/build_fonts.py <dir>
# Sources: notofonts.github.io (static unhinted Regular builds), google/fonts (per-family OFL.txt),
# notofonts/noto-cjk (SubsetOTF Regular + LICENSE).
set -e
dir="$1"
[ -n "$dir" ] || { echo "usage: $0 <empty_download_dir>"; exit 2; }
mkdir -p "$dir"
cd "$dir"
B=https://raw.githubusercontent.com/notofonts/notofonts.github.io/main/fonts
for f in NotoSans NotoSansArabic NotoSansArmenian NotoSansAvestan NotoSansBengali NotoSansCanadianAboriginal \
         NotoSansDevanagari NotoSansEthiopic NotoSansGeorgian NotoSansGujarati NotoSansGurmukhi NotoSansHebrew \
         NotoSansKhmer NotoSansKannada NotoSansLao NotoSansMalayalam NotoSansMyanmar NotoSansOriya NotoSansSinhala \
         NotoSansTamil NotoSansTelugu NotoSansThaana NotoSansThai NotoSerifTibetan NotoSansYi; do
  curl -sfL -o "$f-Regular.ttf" "$B/$f/unhinted/ttf/$f-Regular.ttf"
  low=$(echo "$f" | tr 'A-Z' 'a-z')
  curl -sfL -o "$f-OFL.txt" "https://raw.githubusercontent.com/google/fonts/main/ofl/$low/OFL.txt"
done
for l in SC JP KR; do
  curl -sfL -o "NotoSans$l-Regular.otf" "https://raw.githubusercontent.com/notofonts/noto-cjk/main/Sans/SubsetOTF/$l/NotoSans$l-Regular.otf"
done
curl -sfL -o NotoSansCJK-LICENSE.txt https://raw.githubusercontent.com/notofonts/noto-cjk/main/Sans/LICENSE
ls -l
