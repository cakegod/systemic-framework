using Systemic.Host;
using Systemic.Save;

namespace Systemic.Tests.Save;

public sealed class ContentFingerprintTests
{
    [Fact]
    public void ModuleSourceBytesAreFingerprintedButUnrelatedFilesAreNot()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry");
        var root = Path.Combine(Path.GetTempPath(), $"systemic-save-content-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            foreach (var file in Directory.EnumerateFiles(source))
            {
                File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
            }

            var content = SystemicTestContent.Load(root);
            using var game = new SystemicGameBuilder(content, content.CreateWorld(11, 1)).Build();
            var codec = new SystemicSaveCodec();
            var saved = codec.Save(game);
            File.WriteAllText(Path.Combine(root, "notes.txt"), "ignored");
            var sameContent = SystemicTestContent.Load(root);
            Assert.Equal(content.ContentFingerprint, sameContent.ContentFingerprint);
            Assert.Equal(11, codec.Load(sameContent, saved).World.Seed);

            File.AppendAllText(Path.Combine(root, "incremental.json"), " ");
            var changed = SystemicTestContent.Load(root);
            Assert.NotEqual(content.ContentFingerprint, changed.ContentFingerprint);
            Assert.Contains("content", Assert.Throws<SystemicSaveException>(() => codec.Load(changed, saved)).Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(11, codec.Load(content, saved).World.Seed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
