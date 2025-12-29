#!/bin/sh
set -e

echo "Building site..."
zola build

echo "Optimizing images..."
for f in $(find public -iname '*.png'); do
  if [ ! -f "$f.webp" ]; then
    convert "$f" -define webp:lossless=true "$f.webp"
  fi
done
for f in $(find public -iname '*.jpg'); do
  if [ ! -f "$f.webp" ]; then
    convert "$f" "$f.webp"
  fi
done

echo "Applying patches..."
sed -i 's!<th><code>&amp;mut T</code></th>!<th><code>\&amp;mut\&nbsp;T</code></th>!' ./public/blog/temporary-shared-mutation/index.html

echo "Build complete. The 'public' directory is ready for deployment."