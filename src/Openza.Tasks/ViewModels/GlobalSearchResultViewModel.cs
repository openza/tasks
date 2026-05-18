using Microsoft.UI.Xaml;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.ViewModels;

public sealed class GlobalSearchResultViewModel(GlobalSearchResult result, SpaceItem? space, bool showSpace)
{
    public GlobalSearchResult Result { get; } = result;
    public string Id => Result.Id;
    public string Title => Result.Title;
    public string Subtitle => Result.Subtitle;
    public string Snippet => Result.Snippet;
    public string SpaceName => space?.Name ?? Result.SpaceId;
    public string Glyph => Result.Kind == GlobalSearchResultKind.Project ? "\uE8B7" : "\uE8A5";
    public Visibility SnippetVisibility => string.IsNullOrWhiteSpace(Snippet) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SpaceVisibility => showSpace ? Visibility.Visible : Visibility.Collapsed;
}
