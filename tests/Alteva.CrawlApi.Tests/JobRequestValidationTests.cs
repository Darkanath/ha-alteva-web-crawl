using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Alteva.CrawlApi.Models.Requests;
using FluentAssertions;
using Xunit;

namespace Alteva.CrawlApi.Tests;

public class JobRequestValidationTests
{
    private static IList<ValidationResult> ValidateModel(object model)
    {
        var results = new List<ValidationResult>();
        var context = new ValidationContext(model, serviceProvider: null, items: null);
        Validator.TryValidateObject(model, context, results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void CreateCrawlJobRequest_WithValidData_ShouldPassValidation()
    {
        var request = new CreateCrawlJobRequest
        {
            Url = "https://example.com",
            MaxDepth = 2
        };

        var errors = ValidateModel(request);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void CreateCrawlJobRequest_WhenMaxDepthIsOmitted_ShouldDefaultToTwo()
    {
        var request = new CreateCrawlJobRequest
        {
            Url = "https://example.com"
        };

        request.MaxDepth.Should().Be(2);
        var errors = ValidateModel(request);
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void CreateCrawlJobRequest_WithEmptyUrl_ShouldFailValidation(string? url)
    {
        var request = new CreateCrawlJobRequest
        {
            Url = url!
        };

        var errors = ValidateModel(request);
        errors.Should().Contain(e => e.MemberNames.Contains(nameof(CreateCrawlJobRequest.Url)));
    }

    [Theory]
    [InlineData("not-a-valid-url")]
    [InlineData("just text with spaces")]
    public void CreateCrawlJobRequest_WithMalformedUrl_ShouldFailValidation(string invalidUrl)
    {
        var request = new CreateCrawlJobRequest
        {
            Url = invalidUrl
        };

        var errors = ValidateModel(request);
        errors.Should().Contain(e => e.MemberNames.Contains(nameof(CreateCrawlJobRequest.Url)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    public void CreateCrawlJobRequest_WithOutOfRangeMaxDepth_ShouldFailValidation(int invalidDepth)
    {
        var request = new CreateCrawlJobRequest
        {
            Url = "https://example.com",
            MaxDepth = invalidDepth
        };

        var errors = ValidateModel(request);
        errors.Should().Contain(e => e.MemberNames.Contains(nameof(CreateCrawlJobRequest.MaxDepth)));
    }
}
