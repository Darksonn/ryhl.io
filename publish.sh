#!/bin/sh

zola build

for f in $(find public -iname '*.png'); do
  convert "$f" -define webp:lossless=true "$f.webp"
done
for f in $(find public -iname '*.jpg'); do
  convert "$f" "$f.webp"
done

sed -i 's!<th><code>&amp;mut T</code></th>!<th><code>\&amp;mut\&nbsp;T</code></th>!' ./public/blog/temporary-shared-mutation/index.html

rsync -avz --delete ./public/* ./public/.well-known nine:/var/www/html

