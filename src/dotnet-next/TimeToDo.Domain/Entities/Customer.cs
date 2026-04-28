using ErrorOr;
using TimeToDo.Domain.Errors;

namespace TimeToDo.Domain.Entities;

/// <summary>
/// Simple domain entity for Customer.
/// </summary>
public class Customer
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public bool IsImportant { get; private set; }
    public bool IsArchived { get; private set; }

    private Customer() { }

    /// <summary>
    /// Factory method to create a new Customer with validation.
    /// </summary>
    public static ErrorOr<Customer> Create(
        string name,
        bool isImportant = false,
        Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return DomainErrors.Customer.NameRequired;

        return new Customer
        {
            Id = id ?? Guid.NewGuid(),
            Name = name,
            IsImportant = isImportant,
            IsArchived = false
        };
    }

    /// <summary>
    /// Reconstitutes a Customer from persistence data.
    /// </summary>
    public static Customer Reconstitute(
        Guid id,
        string name,
        bool isImportant,
        bool isArchived)
    {
        return new Customer
        {
            Id = id,
            Name = name,
            IsImportant = isImportant,
            IsArchived = isArchived
        };
    }

    /// <summary>
    /// Updates the Customer's properties.
    /// </summary>
    public ErrorOr<Success> Update(string name, bool isImportant)
    {
        if (string.IsNullOrWhiteSpace(name))
            return DomainErrors.Customer.NameRequired;

        Name = name;
        IsImportant = isImportant;

        return Result.Success;
    }

    /// <summary>
    /// Archives this customer.
    /// </summary>
    public void Archive()
    {
        IsArchived = true;
    }

    /// <summary>
    /// Unarchives this customer.
    /// </summary>
    public void Unarchive()
    {
        IsArchived = false;
    }
}
