﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.ComponentModel.Composition;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.DotNet.ImageBuilder
{
    [Export(typeof(IManifestToolService))]
    public class ManifestToolService : IManifestToolService
    {
        public void PushFromSpec(string manifestFile, bool isDryRun)
        {
            // ExecuteWithRetry because the manifest-tool fails periodically while communicating
            // with the Docker Registry.
            ExecuteHelper.ExecuteWithRetry("manifest-tool", $"push from-spec {manifestFile}", isDryRun);
        }

        public JArray Inspect(string image, bool isDryRun)
        {
            string output = ExecuteHelper.ExecuteWithRetry("manifest-tool", $"inspect {image} --raw", isDryRun);
            return JsonConvert.DeserializeObject<JArray>(output);
        }
    }
}
