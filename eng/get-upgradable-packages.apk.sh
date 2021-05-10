#!/usr/bin/env bash

outputPath="$1"
args=( $@ )
packages=( "${args[@]:1}" )

declare -A upgradablePackages

echo "Updating package cache..."
apk update

# Find all installed package names and store the output in an array
mapfile -t apkPackageNames < <(apk info | sort)

# Find all upgradable packages
apkPackages=$(apk version | sort | tail -n +2)

# Regex to parse the output from apt to get the package name, current version, and upgrade version
upgradablePackages=()

echo
echo "Upgradable packages:"
for pkgName in "${apkPackageNames[@]}"
do
    regex="$pkgName-(\S+)\s+\S+\s+(\S+)"
    if [[ $apkPackages =~ $regex ]]; then
        currentVersion=${BASH_REMATCH[1]}
        upgradeVersion=${BASH_REMATCH[2]}
        
        versionInfo=$(echo "Current: $currentVersion, Upgrade: $upgradeVersion")
        upgradablePackages[$pkgName]=$versionInfo

        echo "Name: $pkgName, $versionInfo"
    fi
done

echo
echo "Packages to upgrade:"

packagesToUpgrade=()
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
