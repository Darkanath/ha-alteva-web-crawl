using Alteva.Domain.Entities;
using Alteva.Domain.Services;
using FluentAssertions;
using Xunit;

namespace Alteva.Domain.UnitTests;

public class JobTreeBuilderTests
{
    private const string Root = "https://example.com/";
    private const string A = "https://example.com/a";
    private const string B = "https://example.com/b";
    private const string C = "https://example.com/c";

    private readonly JobTreeBuilder _builder = new();

    private static Page Done(string url, double ratio) => new() { Url = url, Status = PageStatus.Done, DomainLinkRatio = ratio };

    private static Edge Link(string parent, string child) => new() { ParentUrl = parent, ChildUrl = child };

    [Fact]
    public void BuildTree_ShouldReturnHierarchicalTree()
    {
        var pages = new List<Page> { Done(Root, 1.0), Done(A, 0.5), Done(C, 0.0) };
        var edges = new List<Edge> { Link(Root, A), Link(A, C) };

        var tree = _builder.BuildTree(Root, pages, edges);

        tree.Should().NotBeNull();
        tree!.Url.Should().Be(Root);
        tree.DomainLinkRatio.Should().Be(1.0);
        tree.Depth.Should().Be(0);
        tree.Children.Should().ContainSingle().Which.Url.Should().Be(A);
        tree.Children[0].Children.Should().ContainSingle().Which.Depth.Should().Be(2);
    }

    [Fact]
    public void BuildTree_PlacesEachPageOnce_UnderItsFirstBreadthFirstParent()
    {
        // B is linked from both the root (depth 1) and A; C from both A and B
        var pages = new List<Page> { Done(Root, 1.0), Done(A, 1.0), Done(B, 1.0), Done(C, 1.0) };
        var edges = new List<Edge> { Link(Root, A), Link(Root, B), Link(A, B), Link(A, C), Link(B, C), Link(C, Root) };

        var tree = _builder.BuildTree(Root, pages, edges)!;

        tree.Children.Select(c => c.Url).Should().Equal(A, B);
        tree.Children[0].Children.Select(c => c.Url).Should().Equal(C);
        tree.Children[1].Children.Should().BeEmpty();
        CountNodes(tree).Should().Be(4);
    }

    [Fact]
    public void BuildTree_DenselyLinkedSite_StaysLinearInPageCount()
    {
        // Every page links to every other page - the case that previously grew exponentially
        var urls = Enumerable.Range(0, 60).Select(i => i == 0 ? Root : $"https://example.com/p{i}").ToList();
        var pages = urls.Select(u => Done(u, 1.0)).ToList();
        var edges = urls.SelectMany(p => urls.Where(c => c != p).Select(c => Link(p, c))).ToList();

        var tree = _builder.BuildTree(Root, pages, edges)!;

        CountNodes(tree).Should().Be(60);
        tree.Children.Should().HaveCount(59);
    }

    [Fact]
    public void BuildTree_OmitsLinksThatAreNotPages_AndHasNoRatioForUnfinishedPages()
    {
        var pages = new List<Page>
        {
            Done(Root, 0.5),
            new() { Url = A, Status = PageStatus.Failed, FailureReason = "HTTP 404" },
            new() { Url = B, Status = PageStatus.Skipped }
        };
        var edges = new List<Edge> { Link(Root, A), Link(Root, B), Link(Root, "https://external.test/") };

        var tree = _builder.BuildTree(Root, pages, edges)!;

        tree.Children.Select(c => (c.Url, c.Status, c.DomainLinkRatio)).Should().Equal(
            (A, PageStatus.Failed, (double?)null),
            (B, PageStatus.Skipped, (double?)null));
    }

    [Fact]
    public void BuildTree_WhenRootPageMissing_ReturnsNull()
    {
        _builder.BuildTree(Root, [Done(A, 1.0)], []).Should().BeNull();
    }

    private static int CountNodes(Alteva.Domain.Models.JobTreeNode node) => 1 + node.Children.Sum(CountNodes);
}
