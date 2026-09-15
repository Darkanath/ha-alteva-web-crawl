using Alteva.Domain.Entities;
using Alteva.Domain.Services;
using FluentAssertions;
using Xunit;

namespace Alteva.Domain.UnitTests;

public class JobTreeBuilderTests
{
    private readonly JobTreeBuilder _builder = new(new UrlNormalizer());

    [Fact]
    public void BuildTree_ShouldReturnHierarchicalTree()
    {
        var jobId = Guid.NewGuid();
        var rootUrl = "https://example.com";
        var page1 = "https://example.com/a";
        var page2 = "https://example.com/b";

        var pages = new List<Page>
        {
            new() { JobId = jobId, Url = "https://example.com/", DomainLinkRatio = 1.0 },
            new() { JobId = jobId, Url = page1, DomainLinkRatio = 0.5 },
            new() { JobId = jobId, Url = page2, DomainLinkRatio = 0.0 }
        };

        var edges = new List<Edge>
        {
            new() { JobId = jobId, ParentUrl = "https://example.com/", ChildUrl = page1 },
            new() { JobId = jobId, ParentUrl = "https://example.com/", ChildUrl = page2 },
            new() { JobId = jobId, ParentUrl = page1, ChildUrl = page2 }
        };

        var tree = _builder.BuildTree(rootUrl, pages, edges);

        tree.Should().NotBeNull();
        tree!.Url.Should().Be("https://example.com/");
        tree.DomainLinkRatio.Should().Be(1.0);
        tree.Children.Should().HaveCount(2);

        var childA = tree.Children.FirstOrDefault(c => c.Url == page1);
        childA.Should().NotBeNull();
        childA!.Children.Should().HaveCount(1);
        childA.Children[0].Url.Should().Be(page2);
    }

    [Fact]
    public void BuildTree_WithCircularLinks_ShouldNotInfiniteLoop()
    {
        var jobId = Guid.NewGuid();
        var rootUrl = "https://example.com";
        var page1 = "https://example.com/a";

        var pages = new List<Page>
        {
            new() { JobId = jobId, Url = "https://example.com/", DomainLinkRatio = 1.0 },
            new() { JobId = jobId, Url = page1, DomainLinkRatio = 1.0 }
        };

        // Cycle: root -> A -> root
        var edges = new List<Edge>
        {
            new() { JobId = jobId, ParentUrl = "https://example.com/", ChildUrl = page1 },
            new() { JobId = jobId, ParentUrl = page1, ChildUrl = "https://example.com/" }
        };

        var tree = _builder.BuildTree(rootUrl, pages, edges);

        tree.Should().NotBeNull();
        tree!.Children.Should().HaveCount(1);
        // Cycle is broken: childA does not re-add root
        tree.Children[0].Children.Should().BeEmpty();
    }
}
