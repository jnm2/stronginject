using Microsoft.CodeAnalysis;

namespace StrongInject.Generator
{
    abstract internal record DecoratorSource(int DecoratedParameter, bool Dispose, bool IsAsync)
    {
        public abstract ITypeSymbol OfType { get; }

        public abstract IParameterSymbol GetDecoratedParameter();
    }

    internal record DecoratorRegistration(
        INamedTypeSymbol Type,
        ITypeSymbol DecoratedType,
        bool RequiresInitialization,
        IMethodSymbol Constructor,
        int DecoratedParameter,
        bool Dispose,
        bool IsAsync) : DecoratorSource(DecoratedParameter, Dispose, IsAsync)
    {
        public override ITypeSymbol OfType => DecoratedType;

        public override IParameterSymbol GetDecoratedParameter()
        {
            return Constructor.Parameters[DecoratedParameter];
        }
    }

    internal record DecoratorFactoryMethod(
        IMethodSymbol Method,
        ITypeSymbol DecoratedType,
        bool IsOpenGeneric,
        int DecoratedParameter,
        bool Dispose,
        bool IsAsync) : DecoratorSource(DecoratedParameter, Dispose, IsAsync)
    {
        public override ITypeSymbol OfType => DecoratedType;

        public override IParameterSymbol GetDecoratedParameter()
        {
            return Method.Parameters[DecoratedParameter];
        }
    }
}
