using Cipolatti.API.ExPonto;

namespace Cipolatti.API.Tests;

public sealed class ExPontoSqlTests
{
    [Fact]
    public void Date_parameter_uses_a_dapper_supported_type_without_changing_the_date()
    {
        var date = new DateOnly(2026, 10, 16);

        var parameter = ExPontoSql.DateParameter(date);

        Assert.Equal(typeof(DateTime), parameter.GetType());
        Assert.Equal(new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Unspecified), parameter);
    }

    [Theory]
    [InlineData(null, 0, false)]
    [InlineData(0, 0, true)]
    [InlineData(1, 0, false)]
    [InlineData(1, 1, true)]
    public void Function_limit_marks_zero_capacity_as_extra(int? limite, int usados, bool expected)
    {
        Assert.Equal(expected, ExPontoSql.IsFuncaoExtra(limite, usados));
    }
}
