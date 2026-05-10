using Openza.Tasks.Core.Services;

namespace Openza.Tasks.Tests;

public sealed class MarkdownTaskParserTests
{
    [Fact]
    public void Parse_extracts_gfm_checkboxes()
    {
        var tasks = MarkdownTaskParser.Parse("""
            # Tasks
            - [ ] Write docs
            - [x] Ship release
            * [X] Review PR
            + [ ] Follow up
            """);

        Assert.Equal(4, tasks.Count);
        Assert.Equal("Write docs", tasks[0].Title);
        Assert.False(tasks[0].IsCompleted);
        Assert.True(tasks[1].IsCompleted);
        Assert.True(tasks[2].IsCompleted);
    }

    [Fact]
    public void Parse_skips_extremely_long_lines()
    {
        var tasks = MarkdownTaskParser.Parse("- [ ] " + new string('a', 10_001));

        Assert.Empty(tasks);
    }
}
