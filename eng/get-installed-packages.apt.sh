#!/usr/bin/env bash

apt list --installed 2>/dev/null | grep installed | cut -d/ --fields=1
