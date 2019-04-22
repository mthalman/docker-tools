using System.Linq;
using System.Threading.Tasks;

namespace Microsoft.DotNet.ImageBuilder.Commands
{
    public class ImportCommand : Command<ImportOptions>
    {
        public override Task ExecuteAsync()
        {
            var tags = Manifest.GetFilteredImages()
                .SelectMany(i => i.FilteredPlatforms)
                .SelectMany(p => p.Tags)
                .Where(t => !t.Model.IsLocal);

            ImageImportExportHelper.ImportImages(tags, Options.ImportPath, Options.IsDryRun);

            return Task.CompletedTask;
        }
    }
}
