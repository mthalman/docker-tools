using System.CommandLine;

namespace Microsoft.DotNet.ImageBuilder.Commands
{
    public class ImportOptions : Options, IFilterableOptions
    {
        protected override string CommandHelp => "Imports images into the local Docker host that have been exported to a tar file via the build command with export option.";

        public string ImportPath { get; set; }

        public ManifestFilterOptions FilterOptions { get; } = new ManifestFilterOptions();

        public override void ParseCommandLine(ArgumentSyntax syntax)
        {
            base.ParseCommandLine(syntax);

            FilterOptions.ParseCommandLine(syntax);

            string importPath = null;
            syntax.DefineOption("import-path", ref importPath, "Path of the folder containing the image tar files to be imported.");
            ImportPath = importPath;
        }
    }
}
