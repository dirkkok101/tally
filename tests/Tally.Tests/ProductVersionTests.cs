using Xunit;

namespace Tally.Tests;

public sealed class ProductVersionTests
{
    [Fact]
    public void Product_version_is_semver_with_three_numeric_components()
    {
        var parts = ProductVersion.Current.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.All(parts, part => Assert.True(int.TryParse(part, out _)));
    }

    [Fact]
    public void Minor_matches_implemented_module_count_before_1_0_0()
    {
        var version = Version.Parse(ProductVersion.Current);
        Assert.Equal(0, version.Major);
        Assert.Equal(ProductVersion.ImplementedModules.Count, version.Minor);
        Assert.Equal(3, version.Minor);
        Assert.True(ProductVersion.ImplementedModules.Count < ProductVersion.PlannedModules.Count);
    }

    [Fact]
    public void Implemented_modules_are_a_prefix_of_the_planned_module_line()
    {
        Assert.Equal(
            ProductVersion.PlannedModules.Take(ProductVersion.ImplementedModules.Count),
            ProductVersion.ImplementedModules);
    }

    [Fact]
    public void Contract_line_stays_independent_of_product_semver()
    {
        Assert.Equal("1.0", ProductVersion.ContractVersion);
        Assert.Equal("1.0", ProductVersion.Compatibility);
        Assert.NotEqual(ProductVersion.ContractVersion, ProductVersion.Current);
    }
}
