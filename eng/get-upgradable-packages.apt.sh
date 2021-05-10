#!/usr/bin/env bash

outputPath="$1"
args=( $@ )
packages=( "${args[@]:1}" )

declare -A upgradablePackages

echo "Updating package cache..."
apt update

grep security /etc/apt/sources.list > /tmp/security.list

# Find all upgradable packages from security feeds and store the output in an array
mapfile -t aptPackages < <(apt upgrade -oDir::Etc::Sourcelist=/tmp/security.list -s 2>/dev/null | grep Inst | sort)

# Regex to parse the output from apt to get the package name, current version, and upgrade version
regex="Inst\s(\S+)\s\[(\S+)]\s\((\S+)\s"
upgradablePackages=()

echo
echo "Upgradable packages:"
for pkg in "${aptPackages[@]}"
do
    if [[ $pkg =~ $regex ]]; then
        pkgName=${BASH_REMATCH[1]}
        currentVersion=${BASH_REMATCH[2]}
        upgradeVersion=${BASH_REMATCH[3]}
        
        versionInfo=$(echo "Current: $currentVersion, Upgrade: $upgradeVersion")
        upgradablePackages[$pkgName]=$versionInfo

        echo "Name: $pkgName, $versionInfo"
    fi
done

echo
echo "Packages to upgrade:"

packagesToUpgrade=()

# Lookup the provided package names to see if any are in the list of upgradable packages
for pkgName in "${packages[@]}"
do
    versionInfo=${upgradablePackages[$pkgName]}
    if [ ! -z "$versionInfo" ]; then
        packagesToUpgrade+=($pkgName)

        echo "Name: $pkgName, $versionInfo"

    fi
done

upgradeCount=${#packagesToUpgrade[@]}
if [ $upgradeCount = 0 ]; then
    echo "<none>"
fi

printf "%s\n" "${packagesToUpgrade[@]}" > $outputPath

