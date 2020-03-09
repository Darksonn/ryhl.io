#!/bin/sh

zola build
rsync -avz --delete ./public/* wptest:/var/www/html

