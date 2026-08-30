using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfCore.Interceptors.Materialization;

/// <summary>
/// Substitutes constructor bindings with registered factory methods — for legacy/immutable types
/// that lack a parameterless constructor EF cannot bind otherwise:
/// <code>
/// options.UseEfInterceptors(s => s.WithConstructorFactories(
///     new Dictionary&lt;Type, Func&lt;object&gt;&gt; { [typeof(LegacyItem)] = () =&gt; new LegacyItem("factory") }));
/// </code>
/// EF constructs the instance through the factory and then binds column values onto it as usual.
/// </summary>
public class FactoryMethodInstantiationBindingInterceptor(
    IReadOnlyDictionary<Type, Func<object>> factoriesByType) : IInstantiationBindingInterceptor
{
    private readonly IReadOnlyDictionary<Type, Func<object>> _factories = factoriesByType;
    private readonly ConcurrentDictionary<Type, MethodInfo> _factoryMethods = new();

    public InstantiationBinding ModifyBinding(
        InstantiationBindingInterceptionData interceptionData,
        InstantiationBinding binding)
    {
        var clrType = interceptionData.TypeBase.ClrType;
        if (!_factories.TryGetValue(clrType, out _))
        {
            return binding;
        }

        var createMethod = _factoryMethods.GetOrAdd(clrType, t =>
            typeof(FactoryMethodInstantiationBindingInterceptor)
                .GetMethod(nameof(CreateInstance), BindingFlags.Instance | BindingFlags.NonPublic)!
                .MakeGenericMethod(t));

        return new FactoryMethodBinding(this, createMethod, binding.ParameterBindings);
    }

    // Invoked by EF through the FactoryMethodBinding created above.
#pragma warning disable IDE0051
    private T CreateInstance<T>() where T : class =>
        _factories[typeof(T)]() as T
        ?? throw new InvalidOperationException(
            $"Factory for '{typeof(T).Name}' returned an incompatible instance.");
#pragma warning restore IDE0051

    /// <summary>
    /// EF10-compatible binding that delegates instance creation to a factory method.
    /// Overrides CreateConstructorExpression to emit a call to the factory instead of a constructor.
    /// </summary>
    private sealed class FactoryMethodBinding : InstantiationBinding
    {
        private readonly FactoryMethodInstantiationBindingInterceptor _interceptor;
        private readonly MethodInfo _createMethod;
        private readonly Type _runtimeType;

        public FactoryMethodBinding(
            FactoryMethodInstantiationBindingInterceptor interceptor,
            MethodInfo createMethod,
            IReadOnlyList<ParameterBinding> parameterBindings)
            : base(parameterBindings)
        {
            _interceptor = interceptor;
            _createMethod = createMethod;
            _runtimeType = createMethod.ReturnType;
        }

        public override Type RuntimeType => _runtimeType;

        public override InstantiationBinding With(IReadOnlyList<ParameterBinding> parameterBindings)
            => new FactoryMethodBinding(_interceptor, _createMethod, parameterBindings);

        public override Expression CreateConstructorExpression(ParameterBindingInfo bindingInfo)
        {
            // Factory has no parameters; ignore bindingInfo and just call the factory.
            // EF will then apply property bindings on top of the created instance.
            return Expression.Call(Expression.Constant(_interceptor), _createMethod);
        }
    }
}
