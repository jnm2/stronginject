﻿using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StrongInject.Generator.Visitors
{
    internal class DependencyCheckerVisitor : BaseVisitor<DependencyCheckerVisitor.State>
    {
        private readonly HashSet<InstanceSource> _visited = new();
        private readonly ITypeSymbol _target;
        protected readonly InstanceSourcesScope _containerScope;
        private readonly Action<Diagnostic> _reportDiagnostic;
        private readonly Location _location;
        private readonly HashSet<InstanceSource> _currentlyVisiting = new();
        private bool _anyErrors;

        protected DependencyCheckerVisitor(ITypeSymbol target, InstanceSourcesScope containerScope, Action<Diagnostic> reportDiagnostic, Location location)
        {
            _target = target;
            _containerScope = containerScope;
            _reportDiagnostic = reportDiagnostic;
            _location = location;
        }

        public static bool HasCircularOrMissingDependencies(ITypeSymbol target, bool isAsync, InstanceSourcesScope containerScope, Action<Diagnostic> reportDiagnostic, Location location)
        {
            var visitor = new DependencyCheckerVisitor(target, containerScope, reportDiagnostic, location);
            var state = new State { InstanceSourcesScope = containerScope, IsScopeAsync = isAsync };
            visitor.VisitCore(visitor.GetInstanceSource(target, state, parameterSymbol: null), state);
            return visitor._anyErrors;
        }

        protected override InstanceSource? GetInstanceSource(ITypeSymbol type, State state, IParameterSymbol? parameterSymbol)
        {
            if (!state.InstanceSourcesScope.TryGetSource(type, out var instanceSource, out var ambiguous, out var sourcesNotMatchingConstraints))
            {
                if (ambiguous)
                {
                    _reportDiagnostic(NoBestSourceForType(_location, _target, type));
                    _anyErrors = true;
                }
                else
                {
                    if (parameterSymbol?.IsOptional ?? false)
                    {
                        _reportDiagnostic(InfoNoSourceForOptionalParameter(_location, _target, type));
                    }
                    else
                    {
                        _reportDiagnostic(NoSourceForType(_location, _target, type));
                        _anyErrors = true;
                    }

                    foreach (var sourceNotMatchingConstraints in sourcesNotMatchingConstraints)
                    {
                        _reportDiagnostic(WarnFactoryMethodNotMatchingConstraint(_location, _target, type, sourceNotMatchingConstraints.Method));
                    }
                }
                return null;
            }
            return instanceSource;
        }

        protected override void AfterVisit(InstanceSource source, State state)
        {
            if (source is { IsAsync: true, Scope: not Scope.SingleInstance } and not DelegateSource && !state.IsScopeAsync
                || source is { IsAsync: true, Scope: Scope.SingleInstance } && !state.IsCurrentOrAnyParentScopeAsync)
            {
                _reportDiagnostic(RequiresAsyncResolution(_location, _target, source.OfType));
                _anyErrors = true;
            }

            _currentlyVisiting.Remove(source);
            if (ReferenceEquals(state.InstanceSourcesScope, _containerScope) || ReferenceEquals(state.PreviousScope, _containerScope))
                _visited.Add(source);
        }

        protected override bool ShouldVisitBeforeUpdateState(InstanceSource? source, State state)
        {
            if (source is null)
                return false;
            if (ReferenceEquals(state.InstanceSourcesScope, _containerScope) && _visited.Contains(source))
                return false;
            if (!_currentlyVisiting.Add(source))
            {
                _reportDiagnostic(CircularDependency(_location, _target, source.OfType));
                _anyErrors = true;
                return false;
            }
            if (_currentlyVisiting.Count > MAX_DEPENDENCY_TREE_DEPTH)
            {
                _reportDiagnostic(DependencyTreeTooDeep(_location, _target));
                _anyErrors = true;
                return false;
            }
            return true;
        }

        public override void Visit(DelegateSource delegateSource, State state)
        {
            var (delegateType, returnType, delegateParameters, isAsync) = delegateSource;
#pragma warning disable RS1024 // Compare symbols correctly
            foreach (var paramsWithType in delegateParameters.GroupBy(x => x.Type, (IEqualityComparer<ITypeSymbol>)SymbolEqualityComparer.Default))
#pragma warning restore RS1024 // Compare symbols correctly
            {
                if (paramsWithType.Count() > 1)
                {
                    _reportDiagnostic(DelegateHasMultipleParametersOfTheSameType(_location, _target, delegateType, paramsWithType.Key));
                    _anyErrors = true;
                }
            }

            var returnTypeSource = GetInstanceSource(returnType, state, parameterSymbol: null);

            if (returnTypeSource is DelegateParameter { Parameter: var param })
            {
                if (delegateParameters.Contains(param))
                {
                    _reportDiagnostic(WarnDelegateReturnTypeProvidedBySameDelegate(_location, _target, delegateType, returnType));
                }
                else
                {
                    _reportDiagnostic(WarnDelegateReturnTypeProvidedByAnotherDelegate(_location, _target, delegateType, returnType));
                }
            }
            else if (returnTypeSource?.Scope is Scope.SingleInstance)
            {
                _reportDiagnostic(WarnDelegateReturnTypeIsSingleInstance(_location, _target, delegateType, returnType));
            }

            var usedByDelegateParams = state.UsedParams ??= new(SymbolEqualityComparer.Default);
            state.IsScopeAsync = isAsync;
            VisitCore(returnTypeSource, state);

            foreach (var delegateParam in delegateParameters)
            {
                if (delegateParam.RefKind != RefKind.None)
                {
                    _reportDiagnostic(DelegateParameterIsPassedByRef(_location, _target, delegateType, delegateParam));
                    _anyErrors = true;
                }
                if (!usedByDelegateParams.Contains(delegateParam))
                {
                    _reportDiagnostic(WarnDelegateParameterNotUsed(_location, _target, delegateType, delegateParam.Type, returnType));
                }
            }
        }

        public override void Visit(DelegateParameter delegateParameter, State state)
        {
            state.UsedParams!.Add(delegateParameter.Parameter);
            base.Visit(delegateParameter, state);
        }

        public override void Visit(ArraySource arraySource, State state)
        {
            if (arraySource.Items.Count == 0)
            {
                _reportDiagnostic(WarnNoRegistrationsForElementType(_location, _target, arraySource.ElementType));
            }
            base.Visit(arraySource, state);
        }

        protected override bool ShouldVisitAfterUpdateState(InstanceSource source, State state)
        {
            if (ReferenceEquals(state.InstanceSourcesScope, _containerScope) && !ReferenceEquals(state.PreviousScope, _containerScope) && _visited.Contains(source))
                return false;
            return true;
        }

        public struct State : IState
        {
            private InstanceSourcesScope _instanceSourcesScope;
            private bool _isAsync;

            public InstanceSourcesScope? PreviousScope { get; private set; }
            public InstanceSourcesScope InstanceSourcesScope
            {
                get => _instanceSourcesScope;
                set
                {
                    PreviousScope = _instanceSourcesScope;
                    _instanceSourcesScope = value;
                }
            }
            public bool IsCurrentOrAnyParentScopeAsync { get; private set; }
            public bool IsScopeAsync
            {
                get => _isAsync;
                set
                {
                    _isAsync = value;
                    IsCurrentOrAnyParentScopeAsync |= _isAsync;
                }
            }
            public HashSet<IParameterSymbol>? UsedParams { get; set; }
        }

        private static Diagnostic CircularDependency(Location location, ITypeSymbol target, ITypeSymbol type)
        {
            return Diagnostic.Create(
                new DiagnosticDescriptor(
                        "SI0101",
                        "Type contains circular dependency",
                        "Error while resolving dependencies for '{0}': '{1}' has a circular dependency",
                        "StrongInject",
                        DiagnosticSeverity.Error,
                        isEnabledByDefault: true),
                    location,
                    target,
                    type);
        }

        private static Diagnostic NoSourceForType(Location location, ITypeSymbol target, ITypeSymbol type)
        {
            return Diagnostic.Create(
                new DiagnosticDescriptor(
                        "SI0102",
                        "No source for instance of Type",
                        "Error while resolving dependencies for '{0}': We have no source for instance of type '{1}'",
                        "StrongInject",
                        DiagnosticSeverity.Error,
                        isEnabledByDefault: true),
                    location,
                    target,
                    type);
        }

        private static Diagnostic RequiresAsyncResolution(Location location, ITypeSymbol target, ITypeSymbol type)
        {
            return Diagnostic.Create(
                new DiagnosticDescriptor(
                        "SI0103",
                        "Type can only be resolved asynchronously",
                        "Error while resolving dependencies for '{0}': '{1}' can only be resolved asynchronously.",
                        "StrongInject",
                        DiagnosticSeverity.Error,
                        isEnabledByDefault: true),
                    location,
                    target,
                    type);
        }

        private static Diagnostic DelegateHasMultipleParametersOfTheSameType(Location location, ITypeSymbol target, ITypeSymbol delegateType, ITypeSymbol parameterType)
        {
            return Diagnostic.Create(
                new DiagnosticDescriptor(
                        "SI0104",
                        "Delegate has multiple parameters of same type",
                        "Error while resolving dependencies for '{0}': delegate '{1}' has multiple parameters of type '{2}'.",
                        "StrongInject",
                        DiagnosticSeverity.Error,
                        isEnabledByDefault: true),
                    location,
                    target,
                    delegateType,
                    parameterType);
        }

        private static Diagnostic DelegateParameterIsPassedByRef(Location location, ITypeSymbol target, ITypeSymbol delegateType, IParameterSymbol parameter)
        {
            return Diagnostic.Create(
                new DiagnosticDescriptor(
                        "SI0105",
                        "Parameter of delegate is passed as ref",
                        "Error while resolving dependencies for '{0}': parameter '{1}' of delegate '{2}' is passed as '{3}'.",
                        "StrongInject",
                        DiagnosticSeverity.Error,
                        isEnabledByDefault: true),
                    location,
                    target,
                    parameter,
                    delegateType,
                    parameter.RefKind);
        }

        private static Diagnostic NoBestSourceForType(Location location, ITypeSymbol target, ITypeSymbol type)
        {
            return Diagnostic.Create(
                new DiagnosticDescriptor(
                        "SI0106",
                        "Type contains circular dependency",
                        "Error while resolving dependencies for '{0}': We have multiple sources for instance of type '{1}' and no best source." +
                        " Try adding a single registration for '{1}' directly to the container, and moving any existing registrations for '{1}' on the container to an imported module.",
                        "StrongInject",
                        DiagnosticSeverity.Error,
                        isEnabledByDefault: true),
                    location,
                    target,
                    type);
        }

        private const int MAX_DEPENDENCY_TREE_DEPTH = 200;

        private static Diagnostic DependencyTreeTooDeep(Location location, ITypeSymbol target)
        {
            return Diagnostic.Create(
                new DiagnosticDescriptor(
                        "SI0107",
                        "The Dependency tree is deeper than the Maximum Depth",
                        "Error while resolving dependencies for '{0}': The Dependency tree is deeper than the maximum depth of {1}.",
                        "StrongInject",
                        DiagnosticSeverity.Error,
                        isEnabledByDefault: true),
                    location,
                    target,
                    MAX_DEPENDENCY_TREE_DEPTH);
        }

        private static Diagnostic WarnDelegateParameterNotUsed(Location location, ITypeSymbol target, ITypeSymbol delegateType, ITypeSymbol parameterType, ITypeSymbol delegateReturnType)
        {
            return Diagnostic.Create(
                new DiagnosticDescriptor(
                        "SI1101",
                        "Parameter of delegate is not used in resolution",
                        "Warning while resolving dependencies for '{0}': Parameter '{1}' of delegate '{2}' is not used in resolution of '{3}'.",
                        "StrongInject",
                        DiagnosticSeverity.Warning,
                        isEnabledByDefault: true),
                    location,
                    target,
                    parameterType,
                    delegateType,
                    delegateReturnType);
        }

        private static Diagnostic WarnDelegateReturnTypeProvidedByAnotherDelegate(Location location, ITypeSymbol target, ITypeSymbol delegateType, ITypeSymbol delegateReturnType)
        {
            return Diagnostic.Create(
                new DiagnosticDescriptor(
                        "SI1102",
                        "Return type of delegate is provided as a parameter to another delegate and so will always have the same value",
                        "Warning while resolving dependencies for '{0}': Return type '{1}' of delegate '{2}' is provided as a parameter to another delegate and so will always have the same value.",
                        "StrongInject",
                        DiagnosticSeverity.Warning,
                        isEnabledByDefault: true),
                    location,
                    target,
                    delegateReturnType,
                    delegateType);
        }

        private static Diagnostic WarnDelegateReturnTypeIsSingleInstance(Location location, ITypeSymbol target, ITypeSymbol delegateType, ITypeSymbol delegateReturnType)
        {
            return Diagnostic.Create(
                new DiagnosticDescriptor(
                        "SI1103",
                        "Return type of delegate has a single instance scope and so will always have the same value",
                        "Warning while resolving dependencies for '{0}': Return type '{1}' of delegate '{2}' has a single instance scope and so will always have the same value.",
                        "StrongInject",
                        DiagnosticSeverity.Warning,
                        isEnabledByDefault: true),
                    location,
                    target,
                    delegateReturnType,
                    delegateType);
        }

        private static Diagnostic WarnDelegateReturnTypeProvidedBySameDelegate(Location location, ITypeSymbol target, ITypeSymbol delegateType, ITypeSymbol delegateReturnType)
        {
            return Diagnostic.Create(
                new DiagnosticDescriptor(
                        "SI1104",
                        "Return type of delegate is provided as a parameter to the delegate and so will be returned unchanged",
                        "Warning while resolving dependencies for '{0}': Return type '{1}' of delegate '{2}' is provided as a parameter to the delegate and so will be returned unchanged.",
                        "StrongInject",
                        DiagnosticSeverity.Warning,
                        isEnabledByDefault: true),
                    location,
                    target,
                    delegateReturnType,
                    delegateType);
        }

        private static Diagnostic WarnNoRegistrationsForElementType(Location location, ITypeSymbol target, ITypeSymbol elementType)
        {
            return Diagnostic.Create(
                new DiagnosticDescriptor(
                        "SI1105",
                        "Resolving all registrations of Type, but there are no such registrations",
                        "Warning while resolving dependencies for '{0}': Resolving all registration of type '{1}', but there are no such registrations.",
                        "StrongInject",
                        DiagnosticSeverity.Warning,
                        isEnabledByDefault: true),
                    location,
                    target,
                    elementType);
        }

        private static Diagnostic WarnFactoryMethodNotMatchingConstraint(Location location, ITypeSymbol target, ITypeSymbol type, IMethodSymbol factoryMethodNotMatchingConstraints)
        {
            return Diagnostic.Create(
                new DiagnosticDescriptor(
                        "SI1106",
                        "Factory Method cannot be used to resolve instance of Type as the required type arguments do not satisfy the generic constraints",
                        "Warning while resolving dependencies for '{0}': factory method '{1}' cannot be used to resolve instance of type '{2}' as the required type arguments do not satisfy the generic constraints.",
                        "StrongInject",
                        DiagnosticSeverity.Warning,
                        isEnabledByDefault: true),
                    location,
                    target,
                    factoryMethodNotMatchingConstraints,
                    type);
        }

        private static Diagnostic InfoNoSourceForOptionalParameter(Location location, ITypeSymbol target, ITypeSymbol type)
        {
            return Diagnostic.Create(
                new DiagnosticDescriptor(
                        "SI2100",
                        "No source for instance of Type used in optional parameter",
                        "Info about resolving dependencies for '{0}': We have no source for instance of type '{1}' used in an optional parameter. Using The default value instead.",
                        "StrongInject",
                        DiagnosticSeverity.Info,
                        isEnabledByDefault: true),
                    location,
                    target,
                    type);
        }
    }
}
