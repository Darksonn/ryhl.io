#!/bin/sh
set -e

cp config.toml /tmp/config.toml
sed -i '/base_url/s/ryhl/draft.ryhl/' config.toml
zola build --drafts
mv /tmp/config.toml config.toml

cp ./draft-robots.txt ./public/robots.txt
rsync -avz --delete ./public/* ./public/.well-known nine:/var/www/draft

