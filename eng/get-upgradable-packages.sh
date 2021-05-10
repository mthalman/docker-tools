#!/usr/bin/env bash

scriptDir=$(dirname ${BASH_SOURCE[0]})

if type apt > /dev/null 2>/dev/null; then
    $scriptDir/get-upgradable-packages.apt.sh $@
    exit 0
fi

if type apk > /dev/null 2>/dev/null; then
    $scriptDir/get-upgradable-packages.apk.sh $@
    exit 0
fi

echo "Unsupported package manager. Current supported package managers: apt, apk" >&2
exit 1
