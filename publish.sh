#!/bin/sh

zola build
rsync -avz --delete ./public/* nine:/var/www/html

