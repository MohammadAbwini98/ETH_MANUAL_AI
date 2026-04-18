#!/bin/bash
for port in 5235 5234 5233; do
    case $port in
        5233) label="Portal" ;;
        5234) label="ETH service" ;;
        5235) label="Gold service" ;;
    esac
    if lsof -ti:$port -sTCP:LISTEN &>/dev/null; then
        lsof -ti:$port -sTCP:LISTEN | xargs kill -9
        echo "$label on port $port killed."
    else
        echo "No $label found on port $port."
    fi
done
