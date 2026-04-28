using BuildingBlocks.Helper;
using TimeToDo.Api.Tests.Infrastructure;
using TimeToDo.Application.DTOs.Customer;
using TimeToDo.Domain.Common;

namespace TimeToDo.Api.Tests.Customers;

[Collection(IntegrationTestCollection.Name)]
public class CustomerCrudTests : IntegrationTestBase
{
    public CustomerCrudTests(SharedPostgresFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Create_Customer_ReturnsCreatedCustomer()
    {
        // Arrange
        var createDto = new CustomerCreateDto
        {
            Name = "Acme Corp",
            Important = true
        };

        // Act
        var response = await Client.PostAsJsonAsync("/api/customer", createDto, JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var result = await response.ReadSuccessJsonAsync<CustomerListDto>(JsonOptions);

        Assert.NotNull(result.Id);
        Assert.Equal("Acme Corp", result.Name);
        Assert.True(result.Important);
        Assert.False(result.IsArchived);
    }

    [Fact]
    public async Task Get_AllCustomers_ReturnsAllIncludingArchived()
    {
        // Arrange
        var customer1 = await Factory.CreateTestCustomerAsync("Active Customer");
        var customer2 = await Factory.CreateTestCustomerAsync("Archived Customer");

        // Archive one
        await Client.PutAsJsonAsync(
            "/api/customer/archive",
            new List<string> { new ShortGuid(customer2.Id).ToString() },
            JsonOptions, TestContext.Current.CancellationToken);
        await Factory.WaitForProjectionsAsync();

        // Act
        var response = await Client.GetAsync("/api/customer", TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var result = await response.ReadSuccessJsonAsync<List<CustomerListDto>>(JsonOptions);

        // GetAll returns all customers including archived (per the API behavior)
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task Get_ArchivedCustomers_ReturnsOnlyArchived()
    {
        // Arrange
        await Factory.CreateTestCustomerAsync("Active Customer");
        var archivedCustomer = await Factory.CreateTestCustomerAsync("Archived Customer");

        // Archive one
        await Client.PutAsJsonAsync(
            "/api/customer/archive",
            new List<string> { new ShortGuid(archivedCustomer.Id).ToString() },
            JsonOptions, TestContext.Current.CancellationToken);
        await Factory.WaitForProjectionsAsync();

        // Act
        var response = await Client.GetAsync("/api/customer/archived", TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var result = await response.ReadSuccessJsonAsync<List<CustomerListDto>>(JsonOptions);

        Assert.Single(result);
        Assert.Equal("Archived Customer", result[0].Name);
        Assert.True(result[0].IsArchived);
    }

    [Fact]
    public async Task Get_CustomerById_ReturnsCustomer()
    {
        // Arrange
        var customer = await Factory.CreateTestCustomerAsync("Test Customer", isImportant: true);

        // Act
        var response = await Client.GetAsync($"/api/customer/{new ShortGuid(customer.Id)}", TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var result = await response.ReadSuccessJsonAsync<CustomerDto>(JsonOptions);

        Assert.Equal(new ShortGuid(customer.Id).ToString(), result.Id);
        Assert.Equal("Test Customer", result.Name);
        Assert.True(result.Important);
    }

    [Fact]
    public async Task Get_NonExistentCustomer_ReturnsNotFound()
    {
        // Act
        var response = await Client.GetAsync($"/api/customer/{new ShortGuid(Guid.NewGuid())}", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_Customer_ReturnsUpdatedCustomer()
    {
        // Arrange
        var customer = await Factory.CreateTestCustomerAsync("Original Name", isImportant: false);
        var customerId = new ShortGuid(customer.Id).ToString();

        var updateDto = new CustomerUpdateDto
        {
            Name = new Optional<string>("Updated Name"),
            Important = new Optional<bool>(true)
        };

        // Act
        var response = await Client.PutAsJsonAsync($"/api/customer/{customerId}", updateDto, JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var result = await response.ReadSuccessJsonAsync<CustomerListDto>(JsonOptions);

        Assert.Equal("Updated Name", result.Name);
        Assert.True(result.Important);
    }

    [Fact]
    public async Task Archive_Customer_SetsIsArchivedTrue()
    {
        // Arrange
        var customer = await Factory.CreateTestCustomerAsync("To Archive");
        var customerId = new ShortGuid(customer.Id).ToString();

        // Act
        var response = await Client.PutAsJsonAsync(
            "/api/customer/archive",
            new List<string> { customerId },
            JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        await Factory.WaitForProjectionsAsync();

        // Verify
        var getResponse = await Client.GetAsync($"/api/customer/{customerId}", TestContext.Current.CancellationToken);
        var result = await getResponse.ReadSuccessJsonAsync<CustomerDto>(JsonOptions);
        Assert.True(result.IsArchived);
    }

    [Fact]
    public async Task Restore_Customer_SetsIsArchivedFalse()
    {
        // Arrange
        var customer = await Factory.CreateTestCustomerAsync("To Restore");
        var customerId = new ShortGuid(customer.Id).ToString();

        // Archive first
        await Client.PutAsJsonAsync("/api/customer/archive", new List<string> { customerId }, JsonOptions, TestContext.Current.CancellationToken);
        await Factory.WaitForProjectionsAsync();

        // Act - Restore
        var response = await Client.PutAsJsonAsync(
            "/api/customer/archive?restore=true",
            new List<string> { customerId },
            JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        await Factory.WaitForProjectionsAsync();

        // Verify
        var getResponse = await Client.GetAsync($"/api/customer/{customerId}", TestContext.Current.CancellationToken);
        var result = await getResponse.ReadSuccessJsonAsync<CustomerDto>(JsonOptions);
        Assert.False(result.IsArchived);
    }

    [Fact]
    public async Task Archive_SingleById_SetsIsArchivedTrue()
    {
        // Arrange
        var customer = await Factory.CreateTestCustomerAsync("To Archive");
        var customerId = new ShortGuid(customer.Id).ToString();

        // Act - Use single-item POST endpoint
        var response = await Client.PostAsync($"/api/customer/archive/{customerId}", null, TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        await Factory.WaitForProjectionsAsync();

        // Verify
        var getResponse = await Client.GetAsync($"/api/customer/{customerId}", TestContext.Current.CancellationToken);
        var result = await getResponse.ReadSuccessJsonAsync<CustomerDto>(JsonOptions);
        Assert.True(result.IsArchived);
    }

    [Fact]
    public async Task Restore_SingleById_SetsIsArchivedFalse()
    {
        // Arrange
        var customer = await Factory.CreateTestCustomerAsync("To Restore");
        var customerId = new ShortGuid(customer.Id).ToString();

        // Archive first
        await Client.PostAsync($"/api/customer/archive/{customerId}", null, TestContext.Current.CancellationToken);
        await Factory.WaitForProjectionsAsync();

        // Act - Restore using single-item POST endpoint
        var response = await Client.PostAsync($"/api/customer/restore/{customerId}", null, TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        await Factory.WaitForProjectionsAsync();

        // Verify
        var getResponse = await Client.GetAsync($"/api/customer/{customerId}", TestContext.Current.CancellationToken);
        var result = await getResponse.ReadSuccessJsonAsync<CustomerDto>(JsonOptions);
        Assert.False(result.IsArchived);
    }

    [Fact]
    public async Task Get_Customers_WithPagination_ReturnsPagedResults()
    {
        // Arrange
        for (int i = 1; i <= 5; i++)
        {
            await Factory.CreateTestCustomerAsync($"Customer {i}");
        }

        // Act
        var response = await Client.GetAsync("/api/customer?skip=2&take=2", TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var result = await response.ReadSuccessJsonAsync<List<CustomerListDto>>(JsonOptions);

        Assert.Equal(2, result.Count);
    }
}
