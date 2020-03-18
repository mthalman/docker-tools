using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Text;

namespace Microsoft.DotNet.ImageBuilder.Commands
{
    public class ConvertImageInfoOptions : ManifestOptions
    {
        protected override string CommandHelp => "";

        public string ImageInfoPath { get; set; }

        public override void DefineParameters(ArgumentSyntax syntax)
        {
            base.DefineParameters(syntax);

            string imageInfoPath = null;
            syntax.DefineParameter(
                "image-info-path",
                ref imageInfoPath,
                "Image info file path");
            ImageInfoPath = imageInfoPath;
        }
    }
}
