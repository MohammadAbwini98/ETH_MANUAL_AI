#!/bin/bash
lsof -ti:5234 | xargs kill -9 2>/dev/null && echo "Killed process on port 5234" || echo "Nothing running on port 5234"
