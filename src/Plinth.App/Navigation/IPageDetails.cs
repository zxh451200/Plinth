namespace Plinth.App.Navigation;

public sealed record PageDetail(string Label, string Value);

public interface IPageDetails
{
    System.Collections.Generic.IReadOnlyList<PageDetail> Details { get; }
}
