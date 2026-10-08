using Fetchline.Core.Isa;

namespace Fetchline.Tests.Isa;

public class RegistersTests
{
    [Fact]
    public void EveryRegisterReadsBackFromBothOfItsNames()
    {
        for (var i = 0; i < Registers.Count; i++)
        {
            Assert.True(Registers.TryParse(Registers.Name(i, RegisterStyle.Abi), out var fromAbi));
            Assert.Equal(i, fromAbi);
            Assert.True(Registers.TryParse(Registers.Name(i, RegisterStyle.Numeric), out var fromNumeric));
            Assert.Equal(i, fromNumeric);
        }
    }

    [Theory]
    [InlineData("zero", 0)]
    [InlineData("ra", 1)]
    [InlineData("sp", 2)]
    [InlineData("fp", 8)]
    [InlineData("s0", 8)]
    [InlineData("a0", 10)]
    [InlineData("a7", 17)]
    [InlineData("s11", 27)]
    [InlineData("t6", 31)]
    [InlineData("x31", 31)]
    public void KnownNamesMapToTheirNumber(string name, int expected)
    {
        Assert.True(Registers.TryParse(name, out var index));
        Assert.Equal(expected, index);
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("x32")]
    [InlineData("x07")]
    [InlineData("x-1")]
    [InlineData("A0")]
    [InlineData("s12")]
    [InlineData("t7")]
    [InlineData("a8")]
    [InlineData("pc")]
    public void AnythingElseIsNotARegister(string name)
    {
        Assert.False(Registers.TryParse(name, out _));
    }

    [Fact]
    public void TheSuggestionListHasEachSpellingOnce()
    {
        Assert.Equal(65, Registers.AllNames.Count);
        Assert.Equal(65, Registers.AllNames.Distinct().Count());
        Assert.All(Registers.AllNames, name => Assert.True(Registers.TryParse(name, out _)));
    }
}
