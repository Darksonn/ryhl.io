#!/bin/sh

zola build
rsync -avz --delete ./public/* ./public/.well-known nine:/var/www/html

