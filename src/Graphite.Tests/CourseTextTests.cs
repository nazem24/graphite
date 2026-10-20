using Graphite.Core.Canvas;
using Xunit;

namespace Graphite.Tests;

public class CourseTextTests
{
    [Theory]
    [InlineData("Accounting, Controlling, Auditing", "3,135,1.00", "ACA")]                    // a numeric code says nothing: initials
    [InlineData("Accounting, Controlling, Auditing", "ACA", "ACA")]                           // a short word code is used as it is
    [InlineData("International Affairs: Theory and Practice", "3,141,2.00", "IA")]            // the part before the subtitle
    [InlineData("Marketing (Digital Ventures and E-Commerce)", "", "MAR")]                     // one main word: three letters
    [InlineData("Macroeconomics", "", "MAC")]
    [InlineData("Statistics II", "", "STA")]                                                  // roman numeral dropped
    [InlineData("Business Law", "", "BL")]
    [InlineData("Introduction to Corporate Finance and Governance", "", "ICF")]               // little words skipped, at most three initials
    [InlineData("Recht und Wirtschaft", "", "RW")]
    [InlineData("Management Skills", "ms", "MS")]                                             // code in lower case
    [InlineData("Seminar Paper 2", "SEMINAR", "SP")]                                          // code too long for a mark: use the name
    [InlineData("(Seminar)", "", "SEM")]                                                      // nothing before the bracket: use all of it
    [InlineData("", "", "")]
    public void Abbreviation_PicksAShortReadableMark(string name, string code, string expected) =>
        Assert.Equal(expected, CourseText.Abbreviation(name, code));

    [Fact]
    public void AbbreviationSize_ShrinksAsTheMarkGrows()
    {
        Assert.True(CourseText.AbbreviationSize("IA") > CourseText.AbbreviationSize("ACA"));
        Assert.True(CourseText.AbbreviationSize("ACA") > CourseText.AbbreviationSize("ABCD"));
    }
}
