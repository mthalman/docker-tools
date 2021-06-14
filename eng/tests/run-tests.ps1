#!/usr/bin/env pwsh

[cmdletbinding()]
param(
    # Version of the product container image to be tested
    [string]$Version,

    # Architecture of the image to be tested
    [string]$Architecture,

    # OS version of the image to be tested
    [string]$OS,

    # Container registry the images to be tested are tagged with or are to be pulled from
    [string]$Registry,

    # A string that prefixes all repository names.
    # Example:
    #   - Manifest JSON contains a repository name as "my-repo"
    #   - Registry is set to "my-acr.azurecr.io"
    #   - Repro prefix is set to "staging/"
    #   The test should access images from "my-acr.azurecr.io/staging/my-repo"
    [string]$RepoPrefix,

    # Whether the test should explicitly pull images. If this is not set, the expectation should be that
    # the tags already exist on disk on the test machine.
    [switch]$PullImages,

    # File path to an image-info file indicating which images were built.
    [string]$ImageInfoPath,

    # Set of test categories to execute in the test suite.
    # The only category required by the infrastructure is "pre-build" which gets executed before building any images.
    [ValidateSet("functional", "pre-build")]
    [string[]]$TestCategories = @("functional")
)

# Add test execution here
# Tests should output test results into a sub-folder named "TestResults"
