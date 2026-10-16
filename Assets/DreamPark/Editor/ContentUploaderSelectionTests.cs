#if UNITY_EDITOR && !DREAMPARKCORE
using System.IO;
using DreamPark;
using NUnit.Framework;

public sealed class ContentUploaderSelectionTests
{
    [Test]
    public void LastSelectedContentIdPreferenceIsScopedToProject()
    {
        string first = ContentUploaderPanel.ContentIdPrefKeyForProject(
            Path.Combine(Path.GetTempPath(), "First", "Assets"));
        string firstNormalized = ContentUploaderPanel.ContentIdPrefKeyForProject(
            Path.Combine(Path.GetTempPath(), "First", "Other", "..", "Assets"));
        string second = ContentUploaderPanel.ContentIdPrefKeyForProject(
            Path.Combine(Path.GetTempPath(), "Second", "Assets"));

        Assert.That(first, Is.EqualTo(firstNormalized));
        Assert.That(first, Is.Not.EqualTo(second));
        Assert.That(first, Does.StartWith("DreamPark.ContentUploader.LastContentId."));
    }
}
#endif
