using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;
using BuildingBlocks.Helper;

namespace Cocoar.Auth.Api.Helper;

public class ShortGuidModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        if (bindingContext == null)
        {
            throw new ArgumentNullException(nameof(bindingContext));
        }

        var modelName = bindingContext.ModelName;
        var valueProviderResult = bindingContext.ValueProvider.GetValue(modelName);

        if (valueProviderResult == ValueProviderResult.None)
        {
            return Task.CompletedTask;
        }

        bindingContext.ModelState.SetModelValue(modelName, valueProviderResult);

        var value = valueProviderResult.FirstValue;

        // Check if the argument value is null or empty
        if (string.IsNullOrEmpty(value))
        {
            return Task.CompletedTask;
        }

        if (ShortGuid.TryParse(value, out ShortGuid decoded))
        {
            bindingContext.Result = ModelBindingResult.Success(decoded);
        }
        return Task.CompletedTask;

        //if (!int.TryParse(value, out var id))
        //{
        //    // Non-integer arguments result in model state errors
        //    bindingContext.ModelState.TryAddModelError(
        //        modelName, "Author Id must be an integer.");

        //    return Task.CompletedTask;
        //}

        //// Model will be null if not found, including for
        //// out of range id values (0, -3, etc.)
        //var model = _context.Authors.Find(id);
        //bindingContext.Result = ModelBindingResult.Success(model);
        //return Task.CompletedTask;
    }
}

public class ShortGuidBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        if (context == null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        if (context.Metadata.ModelType == typeof(ShortGuid))
        {
            return new BinderTypeModelBinder(typeof(ShortGuidModelBinder));
        }

        return null;
    }
}