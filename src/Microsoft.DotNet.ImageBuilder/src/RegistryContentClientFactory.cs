﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.ComponentModel.Composition;
using Azure.Containers.ContainerRegistry;
using Azure.Identity;

namespace Microsoft.DotNet.ImageBuilder;

#nullable enable
[Export(typeof(IRegistryContentClientFactory))]
[method: ImportingConstructor]
internal class RegistryContentClientFactory(IHttpClientProvider httpClientProvider) : IRegistryContentClientFactory
{
    private readonly IHttpClientProvider _httpClientProvider = httpClientProvider;

    public IRegistryContentClient Create(string registry, string repo, RegistryAuthContext registryAuthContext)
    {
        // Docker Hub's registry has a separate host name for its API
        string apiRegistry = registry == DockerHelper.DockerHubRegistry ?
            DockerHelper.DockerHubApiRegistry :
            registry!;

        string? ownedAcr = registryAuthContext.OwnedAcr;
        if (ownedAcr?.EndsWith(DockerHelper.AcrDomain) == false)
        {
            ownedAcr = $"{ownedAcr}{DockerHelper.AcrDomain}";
        }
        
        if (apiRegistry == ownedAcr)
        {
            // If the target registry is the owned ACR, connect to it with the Azure library API. This handles all the Azure auth.
            return new ContainerRegistryContentClientWrapper(
                new ContainerRegistryContentClient(new Uri($"https://{apiRegistry}"), repo, new DefaultAzureCredential()));
        }
        else
        {
            BasicAuthenticationCredentials? basicAuthCreds = null;

            // Lookup the credentials, if any, for the registry where the image is located
            if (registryAuthContext.Credentials.TryGetValue(registry, out RegistryCredentials? registryCreds))
            {
                basicAuthCreds = new BasicAuthenticationCredentials(registryCreds.Username, registryCreds.Password);
            }

            RegistryHttpClient httpClient = _httpClientProvider.GetRegistryClient();
            return new RegistryServiceClient(apiRegistry, repo, httpClient, basicAuthCreds);
        }
    }
}
