#!/bin/bash
# Development runner script
# Runs Flutter with local environment variables

ENV_FILE=".env.local"

if [ ! -f "$ENV_FILE" ]; then
    echo "Error: $ENV_FILE not found"
    echo "Copy .env.example to .env.local and fill in your credentials:"
    echo "  cp .env.example .env.local"
    exit 1
fi

flutter run --dart-define-from-file="$ENV_FILE" "$@"
