using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Passwordless;
using Passwordless.AspNetCore;
using Passwordless.AspNetIdentity.Example.Authorization;
using Passwordless.AspNetIdentity.Example.DataContext;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddDataContext();
builder.Services.AddIdentity<IdentityUser, IdentityRole>()
    .AddEntityFrameworkStores<PasswordlessContext>()
    .AddPasswordless(builder.Configuration.GetRequiredSection("Passwordless"));

builder.Services.ConfigureApplicationCookie(options =>
{
    options.AccessDeniedPath = "/AccessDenied";
});

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(ElevationRequirement.PolicyName, policy => policy.AddRequirements([new ElevationRequirement()]))
    .AddPolicy(SecondContextRequirement.PolicyName, policy => policy.AddRequirements([new SecondContextRequirement()]));

builder.Services.AddSingleton<IAuthorizationHandler, StepUpAuthorizationHandler>();
builder.Services.AddTransient<IActionContextAccessor, ActionContextAccessor>();

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Authorized");
});

builder.Services.AddSingleton<StepUpContext>();

var app = builder.Build();

// Execute our migrations to generate our `example.db` file with all the required tables.
using var scope = app.Services.CreateScope();
using var dbContext = scope.ServiceProvider.GetRequiredService<PasswordlessContext>();
dbContext.Database.Migrate();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapPasswordless(enableRegisterEndpoint: true);
app.MapRazorPages();
app.MapControllers();

app.MapGet("AccessDenied", AccessRedirect);
app.MapPost("stepup", StepUp);

app.Run();
return;

static IResult AccessRedirect(string returnUrl, HttpContext context, StepUpContext stepUpContext)
{

    if (context.User.Identity!.IsAuthenticated && !string.IsNullOrWhiteSpace(stepUpContext.Purpose))
    {
        return Results.Redirect($"Authorized/StepUp?returnUrl={returnUrl}&context={stepUpContext.Purpose}");
    }

    return Results.Redirect("Account/Login");
}

static async Task<IResult> StepUp(IOptions<PasswordlessOptions> options, HttpContext context, StepUpContext stepUpContext, [FromBody] StepUpRequest request)
{
    var http = new HttpClient
    {
        BaseAddress = new Uri(options.Value.ApiUrl),
        DefaultRequestHeaders = { { "ApiSecret", options.Value.ApiSecret } }
    };

    using var response = await http.PostAsJsonAsync("/stepup/verify", new
    {
        Token = request.StepUpToken,
        Context = request.Purpose
    });

    var token = await response.Content.ReadFromJsonAsync<StepUpToken>();

    var identity = (ClaimsIdentity)context.User.Identity!;
    var existingStepUpClaim = identity.FindFirst(request.Purpose);

    if (existingStepUpClaim != null)
    {
        identity.RemoveClaim(existingStepUpClaim);
    }
    identity.AddClaim(new Claim(request.Purpose, DateTime.UtcNow.Add(TimeSpan.FromMinutes(2)).ToString(CultureInfo.CurrentCulture)));

    stepUpContext.Purpose = string.Empty;

    await context.SignInAsync(IdentityConstants.ApplicationScheme, new ClaimsPrincipal(identity));

    return Results.Redirect(request.ReturnUrl);
}

record StepUpRequest(string StepUpToken, string ReturnUrl, string Purpose);

record StepUpToken(
    string UserId,
    byte[] CredentialId,
    bool Success,
    DateTime Timestamp,
    string RpId,
    string Origin,
    string Device,
    string Country,
    string Nickname,
    DateTime ExpiresAt,
    Guid TokenId,
    string Type,
    string Context) : VerifiedUser(UserId, CredentialId, Success, Timestamp, RpId, Origin, Device, Country, Nickname, ExpiresAt, TokenId, Type);