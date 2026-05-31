using RestoreDB.DataAccess;
using RestoreDB.Domain.Models;
using Xunit;

namespace RestoreDB.Tests;

public class ConnectionStringTests
{
    private readonly SqlDataAccess _data = new();

    [Fact]
    public void SqlAuth_IncludesUserAndPassword_NotIntegratedSecurity()
    {
        var cs = _data.BuildConnectionString(new ConnectionInfo { Server = "srv", User = "sa", Password = "p@ss" });

        Assert.Contains("User ID=sa", cs);
        Assert.Contains("Initial Catalog=master", cs);
        Assert.DoesNotContain("Integrated Security", cs);
    }

    [Fact]
    public void WindowsAuth_UsesIntegratedSecurity_WithoutUserOrPassword()
    {
        var cs = _data.BuildConnectionString(new ConnectionInfo { Server = "srv", UseWindowsAuth = true });

        Assert.Contains("Integrated Security=True", cs);
        Assert.DoesNotContain("User ID", cs);
        Assert.DoesNotContain("Password", cs);
    }

    [Fact]
    public void TrimsServerAndUser()
    {
        var cs = _data.BuildConnectionString(new ConnectionInfo { Server = "  srv  ", User = "  sa  ", Password = "x" });

        Assert.Contains("Data Source=srv", cs);
        Assert.Contains("User ID=sa", cs);
    }
}
