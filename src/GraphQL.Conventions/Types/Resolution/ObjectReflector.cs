using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GraphQL.Conventions.Execution;
using GraphQL.Conventions.Handlers;
using GraphQL.Conventions.Types.Descriptors;
using GraphQL.Conventions.Types.Resolution.Extensions;
using GraphQL.Conventions.Utilities;

namespace GraphQL.Conventions.Types.Resolution
{
    class ObjectReflector
    {
        private const BindingFlags DefaultBindingFlags =
            BindingFlags.Public |
            BindingFlags.Instance |
            BindingFlags.FlattenHierarchy;

        private const BindingFlags DefaultEnumBindingFlags =
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.DeclaredOnly |
            BindingFlags.FlattenHierarchy;

        private readonly ITypeResolver _typeResolver;

        private readonly CachedRegistry<TypeInfo, GraphTypeInfo> _typeCache = new CachedRegistry<TypeInfo, GraphTypeInfo>();

        private readonly MetaDataAttributeHandler _metaDataHandler = new MetaDataAttributeHandler();

        public ObjectReflector(ITypeResolver typeResolver)
        {
            _typeResolver = typeResolver;
        }

        public GraphSchemaInfo GetSchema(TypeInfo typeInfo)
        {
            var queryField = typeInfo.GetProperty("Query");
            var mutationField = typeInfo.GetProperty("Mutation");
            var subscriptionField = typeInfo.GetProperty("Subscription");

            if (queryField == null)
            {
                throw new ArgumentException("Schema has no query type.");
            }

            var schemaInfo = new GraphSchemaInfo();
            _typeResolver.ActiveSchema = schemaInfo;
            schemaInfo.Query = GetType(queryField.PropertyType.GetTypeInfo());
            schemaInfo.Mutation = mutationField != null
                ? GetType(mutationField.PropertyType.GetTypeInfo())
                : null;
            schemaInfo.Subscription = subscriptionField != null
                ? GetType(subscriptionField.PropertyType.GetTypeInfo())
                : null;
            schemaInfo.Types.AddRange(_typeCache.Entities);

            return schemaInfo;
        }

        public GraphTypeInfo GetType(TypeInfo typeInfo)
        {
            var type = _typeCache.GetEntity(typeInfo);
            if (type != null)
            {
                return type;
            }

            type = _typeCache.AddEntity(typeInfo, new GraphTypeInfo(_typeResolver, typeInfo));
            if (type.IsPrimitive && !type.IsEnumerationType)
            {
                return type;
            }

            var isInjectedType = type.TypeRepresentation.AsType() == typeof(IResolutionContext);

            if (!type.IsEnumerationType &&
                !type.IsScalarType &&
                !type.IsInterfaceType &&
                !type.IsUnionType &&
                !isInjectedType)
            {
                DeriveInterfaces(type);
            }

            if ((!type.IsScalarType || type.IsEnumerationType) &&
                !type.IsUnionType &&
                !isInjectedType)
            {
                DeriveFields(type);
            }

            _metaDataHandler.DeriveMetaData(type, GetTypeInfo(typeInfo));

            if (type.IsInterfaceType && !isInjectedType)
            {
                var iface = type.GetTypeRepresentation();
                var types = iface.Assembly.GetTypes().Where(t => iface.IsAssignableFrom(t));
                foreach (var t in types)
                {
                    var ti = t.GetTypeInfo();
                    if (!ti.IsInterface)
                    {
                        GetType(ti);
                    }
                }
            }

            return type;
        }

        public GraphEntityInfo ApplyAttributes(GraphEntityInfo entityInfo)
        {
            var attributeProvider = entityInfo.AttributeProvider is TypeInfo
                ? GetTypeInfo(entityInfo.AttributeProvider)
                : entityInfo.AttributeProvider;
            _metaDataHandler.DeriveMetaData(entityInfo, attributeProvider);
            return entityInfo;
        }

        private void DeriveInterfaces(GraphTypeInfo type)
        {
            var typeInfo = GetTypeInfo(type);
            var nativeInterfaces = typeInfo.GetInterfaces();

            if (nativeInterfaces.Any(iface => iface == typeof(IUserContext)))
            {
                return;
            }

            var interfaces = nativeInterfaces
                .Where(t => IsValidType(t.GetTypeInfo()))
                .Select(iface => GetType(iface.GetTypeInfo()))
                .Where(iface => iface.IsInterfaceType && !iface.IsIgnored);

            foreach (var iface in interfaces)
            {
                type.AddInterface(iface);
            }
        }

        private void DeriveFields(GraphTypeInfo type)
        {
            var typeInfo = GetTypeInfo(type);
            var fieldInfos = type.IsEnumerationType
                ? GetEnumValues(typeInfo)
                : GetFields(typeInfo);
            var addedFields = new HashSet<string>();
            foreach (var fieldInfo in fieldInfos)
            {
                if (addedFields.Contains(fieldInfo.Name))
                {
                    continue;
                }
                fieldInfo.DeclaringType = type;
                type.Fields.Add(fieldInfo);
                addedFields.Add(fieldInfo.Name);
            }
        }

        private TypeInfo GetTypeInfo(GraphTypeInfo type) =>
            GetTypeInfo(type.AttributeProvider);

        private TypeInfo GetTypeInfo(ICustomAttributeProvider attributeProvider) =>
            ((TypeInfo)attributeProvider).GetTypeRepresentation();

        private IEnumerable<GraphFieldInfo> GetFields(TypeInfo typeInfo)
        {
            var properties = typeInfo
                .GetProperties(DefaultBindingFlags)
                .Where(IsValidMember)
                .Where(propertyInfo => !propertyInfo.IsSpecialName);

            return typeInfo
                .GetMethods(DefaultBindingFlags)
                .Where(IsValidMember)
                .Where(methodInfo => !methodInfo.IsSpecialName)
                .Cast<MemberInfo>()
                .Union(properties)
                .Select(DeriveField)
                .Where(field => !field.IsIgnored)
                .OrderBy(field => field.Name);
        }

        private IEnumerable<GraphArgumentInfo> GetArguments(MethodInfo methodInfo)
        {
            foreach (var argument in methodInfo?
                .GetParameters()
                .Select(DeriveArgument)
                ?? new GraphArgumentInfo[0])
            {
                yield return argument;
            }
        }

        private IEnumerable<GraphEnumMemberInfo> GetEnumValues(TypeInfo typeInfo)
        {
            return typeInfo
                .GetEnumNames()
                .Select(name => DeriveEnumValue(name, typeInfo))
                .Where(member => !member.IsIgnored)
                .OrderBy(member => member.Name);
        }

        private GraphFieldInfo DeriveField(MemberInfo memberInfo)
        {
            var field = new GraphFieldInfo(_typeResolver, memberInfo);

            var propertyInfo = memberInfo as PropertyInfo;
            if (propertyInfo != null)
            {
                field.Type = GetType(propertyInfo.PropertyType.GetTypeInfo());
            }

            var fieldInfo = memberInfo as FieldInfo;
            if (fieldInfo != null)
            {
                field.Type = GetType(fieldInfo.FieldType.GetTypeInfo());
            }

            var methodInfo = memberInfo as MethodInfo;
            if (methodInfo != null)
            {
                field.Type = GetType(methodInfo.ReturnType.GetTypeInfo());
                field.Arguments.AddRange(GetArguments(methodInfo));
            }

            _metaDataHandler.DeriveMetaData(field, memberInfo);
            return field;
        }

        private GraphArgumentInfo DeriveArgument(ParameterInfo parameterInfo)
        {
            var parameterType = parameterInfo.ParameterType;
            var parameterTypeInfo = parameterType.GetTypeInfo();
            var argument = new GraphArgumentInfo(_typeResolver, parameterInfo);
            if (parameterType == typeof(object))
            {
                throw new ArgumentException($"Cannot construct argument '{parameterInfo.Name}' of type 'object'; invalid type.");
            }
            else if (parameterTypeInfo.GetInterfaces().Any(iface => iface == typeof(IUserContext)))
            {
                argument.Type = GetType(typeof(IUserContext).GetTypeInfo());
                argument.IsInjected = true;
            }
            else
            {
                argument.Type = GetType(parameterInfo.ParameterType.GetTypeInfo());
            }
            if (argument.Type.TypeRepresentation.AsType() == typeof(IResolutionContext))
            {
                argument.IsInjected = true;
            }
            argument.DefaultValue = parameterInfo.HasDefaultValue ? parameterInfo.DefaultValue : null;
            _metaDataHandler.DeriveMetaData(argument, parameterInfo);
            return argument;
        }

        private GraphEnumMemberInfo DeriveEnumValue(string name, TypeInfo type)
        {
            var memberInfo = type
                .GetMember(name, DefaultEnumBindingFlags)
                .First();
            var enumValue = new GraphEnumMemberInfo(_typeResolver, memberInfo);
            enumValue.Name = name;
            enumValue.Value = Enum.Parse(type.AsType(), enumValue.Name);
            _metaDataHandler.DeriveMetaData(enumValue, memberInfo);
            return enumValue;
        }

        private static bool IsValidType(TypeInfo typeInfo)
        {
            return typeInfo.Namespace != nameof(System) &&
                   !typeInfo.Namespace.StartsWith($"{nameof(System)}.");
        }

        private static bool IsValidMember(MemberInfo memberInfo)
        {
            return memberInfo != null &&
                   memberInfo.DeclaringType != null &&
                   memberInfo.DeclaringType.Namespace != nameof(System) &&
                   !(memberInfo.DeclaringType.Namespace?.StartsWith($"{nameof(System)}.") ?? false) &&
                   !(memberInfo.DeclaringType.GetTypeInfo()?.IsValueType ?? false) &&
                   memberInfo.Name != nameof(object.ToString) &&
                   HasValidReturnType(memberInfo);
        }

        private static bool HasValidReturnType(MemberInfo memberInfo)
        {
            Type returnType;
            if (memberInfo is PropertyInfo)
            {
                returnType = ((PropertyInfo)memberInfo).PropertyType;
            }
            else if (memberInfo is MethodInfo)
            {
                returnType = ((MethodInfo)memberInfo).ReturnType;
            }
            else
            {
                return true;
            }
            return returnType != typeof(object);
        }
    }
}
