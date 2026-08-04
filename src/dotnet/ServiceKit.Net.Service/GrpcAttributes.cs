using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace ServiceKit.Net
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class AutoRegisterGrpcAttribute : Attribute
    {
    }

    public static class RegistrationExtensions
    {
        // Resolved once, and with Single rather than First: should a future Grpc.AspNetCore add
        // another single-argument generic MapGrpcService, picking one of them at random would be far
        // worse than failing here with an explicit message.
        private static readonly MethodInfo _mapGrpcServiceMethod = typeof(GrpcEndpointRouteBuilderExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method =>
                method.Name == nameof(GrpcEndpointRouteBuilderExtensions.MapGrpcService) &&
                method.IsGenericMethodDefinition &&
                method.GetGenericArguments().Length == 1 &&
                method.GetParameters().Length == 1 &&
                method.GetParameters()[0].ParameterType == typeof(IEndpointRouteBuilder));

        /// <summary>
        /// Maps every [AutoRegisterGrpc] class of the given assemblies. With none given it searches
        /// the loaded assemblies that reference this one - the only ones that can carry the attribute.
        /// </summary>
        public static void MapGrpcControllers(this WebApplication app, params Assembly[] assemblies)
        {
            var searched = (assemblies != null && assemblies.Length > 0)
                ? assemblies
                : _CandidateAssemblies();

            var grpcServiceTypes = searched
                .SelectMany(_LoadableTypes)
                .Where(type =>
                    type.IsClass &&
                    type.IsAbstract == false &&
                    type.GetCustomAttribute<AutoRegisterGrpcAttribute>() != null)
                .ToList();

            foreach (var type in grpcServiceTypes)
            {
                try
                {
                    _mapGrpcServiceMethod.MakeGenericMethod(type).Invoke(null, new object[] { app });
                }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    // Unwrapped, so what surfaces is the actual failure and not the reflection
                    // wrapper around it
                    throw new InvalidOperationException($"Mapping the gRPC service '{type.FullName}' failed.", ex.InnerException);
                }
            }

            // Finding nothing used to be silent, and a host whose services never got mapped looks
            // healthy right until the first call. Say it out loud, either way.
            if (grpcServiceTypes.Count == 0)
            {
                app.Logger?.LogWarning(
                    "No [AutoRegisterGrpc] gRPC service was found in {AssemblyCount} assemblies ({Assemblies}). If this host is meant to serve gRPC, pass the assembly to MapGrpcControllers explicitly.",
                    searched.Length,
                    string.Join(", ", searched.Select(assembly => assembly.GetName().Name)));
            }
            else
            {
                app.Logger?.LogInformation(
                    "Mapped {ServiceCount} gRPC service(s): {Services}",
                    grpcServiceTypes.Count,
                    string.Join(", ", grpcServiceTypes.Select(type => type.FullName)));
            }
        }

        public static void MapRestControllers(this IEndpointRouteBuilder app)
        {
            app.MapControllers().RequireAuthorization();
        }

        // The entry assembly alone was not good enough: under a test run it is the test runner, and
        // the services usually live in a referenced project anyway - so the scan found nothing and
        // said nothing about it.
        private static Assembly[] _CandidateAssemblies()
        {
            var ownName = typeof(AutoRegisterGrpcAttribute).Assembly.GetName().Name;

            return AppDomain.CurrentDomain
                .GetAssemblies()
                .Where(assembly =>
                    assembly.IsDynamic == false &&
                    (assembly.GetName().Name == ownName ||
                     assembly.GetReferencedAssemblies().Any(referenced => referenced.Name == ownName)))
                .ToArray();
        }

        // One unloadable type used to take the whole host down at startup. The types that did load
        // are still worth registering.
        private static IEnumerable<Type> _LoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(type => type != null);
            }
        }
    }
}
