using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Text;

namespace Microsoft.DotNet.ImageBuilder.Commands
{
    public class ConvertImageInfoOptions : ManifestOptions, IFilterableOptions
    {
        public ManifestFilterOptions FilterOptions { get; } = new ManifestFilterOptions();

        protected override string CommandHelp => "";

        public string ImageInfoPath { get; set; }

        public string ImageInfoOutputPath { get; set; }

        public bool CreatedDate { get; set; }

        public override void DefineOptions(ArgumentSyntax syntax)
        {
            base.DefineOptions(syntax);
            FilterOptions.DefineOptions(syntax);

            bool createdDate = false;
            syntax.DefineOption("created-date", ref createdDate, "");
            CreatedDate = createdDate;
        }

        public override void DefineParameters(ArgumentSyntax syntax)
        {
            base.DefineParameters(syntax);

            string imageInfoPath = null;
            syntax.DefineParameter("image-info-path", ref imageInfoPath, "");
            ImageInfoPath = imageInfoPath;

            string imageInfoOutputPath = null;
            syntax.DefineParameter(
                "image-info-output-path",
                ref imageInfoOutputPath,
                "Image info file path");
            ImageInfoOutputPath = imageInfoOutputPath;
        }
    }
}
