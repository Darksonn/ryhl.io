#!/bin/sh
set -e

cp config.toml /tmp/config.toml
sed -i '/base_url/s/ryhl/draft.ryhl/' config.toml
zola build --drafts
mv /tmp/config.toml config.toml

cp ./draft-robots.txt ./public/robots.txt

sed -i 's!<th><code>&amp;mut T</code></th>!<th><code>\&amp;mut\&nbsp;T</code></th>!' ./public/blog/temporary-shared-mutation/index.html
find ./public -type f -exec sed -i 's/<meta name="robots" content="index,follow">/<meta name="robots" content="noindex">/' {} \;
rsync -avz --delete ./public/* ./public/.well-known nine:/var/www/draft.ryhl.io

