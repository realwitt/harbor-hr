using Harbor;

namespace Harbor.Tests;

public class SchemaTypesTests
{
    [Fact]
    public void Employee_defaults_match_the_schema()
    {
        var employee = new Employee
        {
            Email = "ew@eliaswitt.com",
            Name = "Elias Witt",
            Jurisdiction = "US-NC",
            HiredOn = new DateOnly(2024, 1, 15),
        };

        Assert.Equal(EmployeeRole.Employee, employee.Role);
        Assert.Equal("America/New_York", employee.Timezone);
        Assert.False(employee.HdhpEligible);
        Assert.Null(employee.HsaCoverage);
    }
}
