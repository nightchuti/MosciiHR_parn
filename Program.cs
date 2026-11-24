using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/database", async (IConfiguration config, CancellationToken token) =>
{
    var settings = new SqlConnectionStringBuilder(config.GetConnectionString("MosciiHR"));
    if (string.IsNullOrEmpty(settings.Password))
        settings.Password = config["Database:Password"] ?? "";
    if (!settings.IntegratedSecurity && string.IsNullOrEmpty(settings.Password))
        return Results.Json(new { message = "รอตั้งค่าบัญชีฐานข้อมูล" }, statusCode: 503);

    try
    {
        await using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = new SqlCommand("SELECT DB_NAME()", connection);
        var name = await command.ExecuteScalarAsync(token);
        return Results.Ok(new { message = $"เชื่อมต่อ {name} สำเร็จ" });
    }
    catch (SqlException)
    {
        return Results.Json(new { message = "เชื่อมต่อไม่ได้ กรุณาตรวจเครือข่าย บัญชี และใบรับรอง SQL Server" }, statusCode: 503);
    }
});
app.Run();
