using System;
using System.Reflection.Emit;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using AntMicro.Migrant.Hooks;

namespace AntMicro.Migrant.Generators
{
	internal class WriteMethodGenerator
	{
		internal WriteMethodGenerator(Type typeToGenerate, IDictionary<Type, int> typeIndices)
		{
			this.typeIndices = typeIndices;
			if(!typeToGenerate.IsArray)
			{
				dynamicMethod = new DynamicMethod("Write", MethodAttributes.Public | MethodAttributes.Static, CallingConventions.Standard,
			                               typeof(void), ParameterTypes, typeToGenerate, true);
			}
			else
			{
				var methodNo = Interlocked.Increment(ref WriteArrayMethodCounter);
				dynamicMethod = new DynamicMethod(string.Format("WriteArray{0}", methodNo), null, ParameterTypes, true);
			}
			generator = dynamicMethod.GetILGenerator();

			// preserialization callbacks
			GenerateInvokeCallbacks(typeToGenerate, typeof(PreSerializationAttribute));

			if(!GenerateSpecialWrite(typeToGenerate))
			{
				GenerateWriteFields(gen =>
				                    {
					gen.Emit(OpCodes.Ldarg_2);
				}, typeToGenerate);
			}

			// postserialization callbacks
			GenerateInvokeCallbacks(typeToGenerate, typeof(PostSerializationAttribute));

			generator.Emit(OpCodes.Ret);
		}

		internal DynamicMethod Method
		{
			get
			{
				return dynamicMethod;
			}
		}

		private void GenerateInvokeCallbacks(Type actualType, Type attributeType)
		{
			var preSerializationMethods = Helpers.GetMethodsWithAttribute(attributeType, actualType);
			foreach(var method in preSerializationMethods)
			{
				if(!method.IsStatic)
				{
					generator.Emit(OpCodes.Ldarg_2); // object to serialize
				}
				generator.Emit(OpCodes.Call, method);
			}
		}

		private void GenerateWriteFields(Action<ILGenerator> putValueToWriteOnTop, Type actualType)
		{
			var fields = actualType.GetAllFields().Where(Helpers.IsNotTransient).OrderBy(x => x.Name); // TODO: unify
			foreach(var field in fields)
			{
				GenerateWriteType(gen => 
				                  {
					putValueToWriteOnTop(gen);
					gen.Emit(OpCodes.Ldfld, field); // TODO: consider putting that in some local variable
				}, field.FieldType);
			}
		}

		private bool GenerateSpecialWrite(Type actualType)
		{
			if(actualType.IsValueType)
			{
				// value type encountered here means it is in fact boxed value type
				// according to protocol it is written as it would be written inlined
				GenerateWriteValue(gen =>
				                   {
					gen.Emit(OpCodes.Ldarg_2); // value to serialize
					gen.Emit(OpCodes.Unbox_Any, actualType);
				}, actualType);
				return true;
			}
			if(actualType.IsArray)
			{
				GenerateWriteArray(actualType);
				return true;
			}
			bool isGeneric, isGenericallyIterable, isDictionary;
			Type elementType;
			if(Helpers.IsCollection(actualType, out elementType, out isGeneric, out isGenericallyIterable, out isDictionary))
			{
				GenerateWriteCollection(elementType, isGeneric, isGenericallyIterable, isDictionary);
				return true;
			}
			return false;
		}

		private void GenerateWriteArray(Type actualType)
		{
			PrimitiveWriter primitiveWriter = null; // TODO

			var elementType = actualType.GetElementType();
			var rank = actualType.GetArrayRank();
			if(rank != 1)
			{
				GenerateWriteMultidimensionalArray(actualType, elementType);
				return;
			}

			generator.DeclareLocal(typeof(int)); // this is for counter
			generator.DeclareLocal(elementType); // this is for the current element
			generator.DeclareLocal(typeof(int)); // length of the array

			// writing rank
			generator.Emit(OpCodes.Ldarg_1); // primitiveWriter
			generator.Emit(OpCodes.Ldc_I4_1);
			generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => primitiveWriter.Write(0)));

			// writing length
			generator.Emit(OpCodes.Ldarg_1); // primitiveWriter
			generator.Emit(OpCodes.Ldarg_2); // array to serialize
			generator.Emit(OpCodes.Castclass, actualType);
			generator.Emit(OpCodes.Ldlen);
			generator.Emit(OpCodes.Dup);
			generator.Emit(OpCodes.Stloc_2);
			generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => primitiveWriter.Write(0)));

			// writing elements
			generator.Emit(OpCodes.Ldc_I4_0);
			generator.Emit(OpCodes.Stloc_0);
			var loopBegin = generator.DefineLabel();
			generator.MarkLabel(loopBegin);
			generator.Emit(OpCodes.Ldarg_2); // array to serialize
			generator.Emit(OpCodes.Castclass, actualType);
			generator.Emit(OpCodes.Ldloc_0); // index
			generator.Emit(OpCodes.Ldelem, elementType);
			generator.Emit(OpCodes.Stloc_1); // we put current element to local variable

			GenerateWriteType(gen =>
			                  {
				gen.Emit(OpCodes.Ldloc_1); // current element
			}, elementType);

			// loop book keeping
			generator.Emit(OpCodes.Ldloc_0); // current index, which will be increased by 1
			generator.Emit(OpCodes.Ldc_I4_1);
			generator.Emit(OpCodes.Add);
			generator.Emit(OpCodes.Stloc_0);
			generator.Emit(OpCodes.Ldloc_0);
			generator.Emit(OpCodes.Ldloc_2); // length of the array
			generator.Emit(OpCodes.Blt, loopBegin);
		}

		private void GenerateWriteMultidimensionalArray(Type actualType, Type elementType)
		{
			Array array = null; // TODO
			PrimitiveWriter primitiveWriter = null; // TODO

			var rank = actualType.GetArrayRank();
			// local for current element
			generator.DeclareLocal(elementType);
			// locals for indices
			var indexLocals = new int[rank];
			for(var i = 0; i < rank; i++)
			{
				indexLocals[i] = generator.DeclareLocal(typeof(int)).LocalIndex;
			}
			// locals for lengths
			var lengthLocals = new int[rank];
			for(var i = 0; i < rank; i++)
			{
				lengthLocals[i] = generator.DeclareLocal(typeof(int)).LocalIndex;
			}

			// writing rank
			generator.Emit(OpCodes.Ldarg_1); // primitiveWriter
			generator.Emit(OpCodes.Ldc_I4, rank);
			generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => primitiveWriter.Write(0)));

			// writing lengths
			for(var i = 0; i < rank; i++)
			{
				generator.Emit(OpCodes.Ldarg_1); // primitiveWriter
				generator.Emit(OpCodes.Ldarg_2); // array to serialize
				generator.Emit(OpCodes.Castclass, actualType);
				generator.Emit(OpCodes.Ldc_I4, i);
				generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => array.GetLength(0)));
				generator.Emit(OpCodes.Dup);
				generator.Emit(OpCodes.Stloc, lengthLocals[i]);
				generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => primitiveWriter.Write(0)));
			}

			// writing elements
			GenerateLoop(0, rank, indexLocals, lengthLocals, actualType, elementType);
		}

		// TODO: rename
		private void GenerateLoop(int currentDimension, int rank, int[] indexLocals, int[] lengthLocals, Type arrayType, Type elementType)
		{
			// initalization
			generator.Emit(OpCodes.Ldc_I4_0);
			generator.Emit(OpCodes.Stloc, indexLocals[currentDimension]);
			var loopBegin = generator.DefineLabel();
			generator.MarkLabel(loopBegin);
			if(currentDimension == rank - 1)
			{
				// writing the element
				generator.Emit(OpCodes.Ldarg_2); // array to serialize
				generator.Emit(OpCodes.Castclass, arrayType);
				for(var i = 0; i < rank; i++)
				{
					generator.Emit(OpCodes.Ldloc, indexLocals[i]);
				}
				generator.Emit(OpCodes.Call, arrayType.GetMethod("Get"));
				generator.Emit(OpCodes.Stloc_0);
				GenerateWriteType(gen => gen.Emit(OpCodes.Ldloc_0), elementType);
			}
			else
			{
				GenerateLoop(currentDimension + 1, rank, indexLocals, lengthLocals, arrayType, elementType);
			}
			// incremeting index and loop exit condition check
			generator.Emit(OpCodes.Ldloc, indexLocals[currentDimension]);
			generator.Emit(OpCodes.Ldc_I4_1);
			generator.Emit(OpCodes.Add);
			generator.Emit(OpCodes.Dup);
			generator.Emit(OpCodes.Stloc, indexLocals[currentDimension]);
			generator.Emit(OpCodes.Ldloc, lengthLocals[currentDimension]);
			generator.Emit(OpCodes.Blt, loopBegin);
		}

		private void GenerateWriteCollection(Type formalElementType, bool isGeneric, bool isGenericallyIterable, bool isIDictionary)
		{
			PrimitiveWriter primitiveWriter = null; // TODO

			var genericTypes = new [] { formalElementType };
			var ifaceType = isGeneric ? typeof(ICollection<>).MakeGenericType(genericTypes) : typeof(ICollection);
			Type enumerableType;
			if(isIDictionary)
			{
				formalElementType = typeof(object); // convenient in our case
				enumerableType = typeof(IDictionary);
			}
			else
			{
				enumerableType = isGenericallyIterable ? typeof(IEnumerable<>).MakeGenericType(genericTypes) : typeof(IEnumerable);
			}
			Type enumeratorType;
			if(isIDictionary)
			{
				enumeratorType = typeof(IDictionaryEnumerator);
			}
			else
			{
				enumeratorType = isGenericallyIterable ? typeof(IEnumerator<>).MakeGenericType(genericTypes) : typeof(IEnumerator);
			}

			generator.DeclareLocal(enumeratorType); // iterator
			generator.DeclareLocal(formalElementType); // current element

			// length of the collection
			generator.Emit(OpCodes.Ldarg_1); // primitiveWriter
			generator.Emit(OpCodes.Ldarg_2); // collection to serialize
			var countMethod = ifaceType.GetProperty("Count").GetGetMethod();
			generator.Emit(OpCodes.Call, countMethod);
			generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => primitiveWriter.Write(0)));

			// elements
			var getEnumeratorMethod = enumerableType.GetMethod("GetEnumerator");
			generator.Emit(OpCodes.Ldarg_2); // collection to serialize
			generator.Emit(OpCodes.Call, getEnumeratorMethod);
			generator.Emit(OpCodes.Stloc_0);
			var loopBegin = generator.DefineLabel();
			generator.MarkLabel(loopBegin);
			generator.Emit(OpCodes.Ldloc_0);
			var finish = generator.DefineLabel();
			// TODO: Helpers.GetMethod?
			generator.Emit(OpCodes.Call, typeof(IEnumerator).GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public));
			generator.Emit(OpCodes.Brfalse, finish);
			generator.Emit(OpCodes.Ldloc_0);
			if(isIDictionary)
			{
				// key
				generator.Emit(OpCodes.Call, enumeratorType.GetProperty("Key").GetGetMethod());
				generator.Emit(OpCodes.Stloc_1);
				GenerateWriteTypeLocal1(generator, formalElementType);

				// value
				generator.Emit(OpCodes.Ldloc_0);
				generator.Emit(OpCodes.Call, enumeratorType.GetProperty("Value").GetGetMethod());
				generator.Emit(OpCodes.Stloc_1);
				GenerateWriteTypeLocal1(generator, formalElementType);
			}
			else
			{
				generator.Emit(OpCodes.Call, enumeratorType.GetProperty("Current").GetGetMethod());
				generator.Emit(OpCodes.Stloc_1);
	
				// operation on current element
				GenerateWriteTypeLocal1(generator, formalElementType);
			}

			generator.Emit(OpCodes.Br, loopBegin);
			generator.MarkLabel(finish);
		}

		private void GenerateWriteTypeLocal1(ILGenerator generator, Type formalElementType)
		{
			GenerateWriteType(gen =>
			                  {
				gen.Emit(OpCodes.Ldloc_1);
			}, formalElementType);
		}

		private void GenerateWriteType(Action<ILGenerator> putValueToWriteOnTop, Type formalType)
		{
			switch(Helpers.GetSerializationType(formalType))
			{
			case SerializationType.Transient:
				// just omit it
				return;
			case SerializationType.Value:
				GenerateWriteValue(putValueToWriteOnTop, formalType);
				break;
			case SerializationType.Reference:
				GenerateWriteReference(putValueToWriteOnTop, formalType);
				break;
			}
		}

		private void GenerateWriteValue(Action<ILGenerator> putValueToWriteOnTop, Type formalType)
		{
			ObjectWriter.CheckLegality(formalType);
			PrimitiveWriter primitiveWriter = null; // TODO

			if(formalType.IsEnum)
			{
				formalType = Enum.GetUnderlyingType(formalType);
			}
			var writeMethod = typeof(PrimitiveWriter).GetMethod("Write", new [] { formalType });
			// if this method is null, then it is a non-primitive (i.e. custom) struct
			if(writeMethod != null)
			{
				generator.Emit(OpCodes.Ldarg_1); // primitive writer waits there
				putValueToWriteOnTop(generator);
				generator.Emit(OpCodes.Call, writeMethod);
				return;
			}
			var nullableUnderlyingType = Nullable.GetUnderlyingType(formalType);
			if(nullableUnderlyingType != null)
			{
				var hasValue = generator.DefineLabel();
				var finish = generator.DefineLabel();
				var localIndex = generator.DeclareLocal(formalType).LocalIndex;
				generator.Emit(OpCodes.Ldarg_1); // primitiveWriter
				putValueToWriteOnTop(generator);
				generator.Emit(OpCodes.Stloc_S, localIndex);
				generator.Emit(OpCodes.Ldloca_S, localIndex);
				generator.Emit(OpCodes.Call, formalType.GetProperty("HasValue").GetGetMethod());
				generator.Emit(OpCodes.Brtrue_S, hasValue);
				generator.Emit(OpCodes.Ldc_I4_0);
				generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => primitiveWriter.Write(false)));
				generator.Emit(OpCodes.Br, finish);
				generator.MarkLabel(hasValue);
				generator.Emit(OpCodes.Ldc_I4_1);
				generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => primitiveWriter.Write(false)));
				GenerateWriteValue(gen =>
				                   {
					generator.Emit(OpCodes.Ldloca_S, localIndex);
					generator.Emit(OpCodes.Call, formalType.GetProperty("Value").GetGetMethod());
				}, nullableUnderlyingType);
				generator.MarkLabel(finish);
				return;
			}
			if(formalType.IsGenericType && formalType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
			{
				var keyValueTypes = formalType.GetGenericArguments();
				var localIndex = generator.DeclareLocal(formalType).LocalIndex;
				GenerateWriteType(gen =>
				                  {
					putValueToWriteOnTop(gen);
					// TODO: is there a better method of getting address?
					// don't think so, looking at
					// http://stackoverflow.com/questions/76274/
					// we *may* do a little optimization if this value write takes
					// place when dictionary is serialized (current KVP is stored in
					// local 1 in such situation); the KVP may be, however, written
					// independently
					gen.Emit(OpCodes.Stloc_S, localIndex);
					gen.Emit(OpCodes.Ldloca_S, localIndex);
					gen.Emit(OpCodes.Call, formalType.GetProperty("Key").GetGetMethod());
				}, keyValueTypes[0]);
				GenerateWriteType(gen =>
				                  {
					// we assume here that the key write was invoked earlier (it should be
					// if we're conforming to the protocol), so KeyValuePair is already
					// stored as local
					gen.Emit(OpCodes.Ldloca_S, localIndex);
					gen.Emit(OpCodes.Call, formalType.GetProperty("Value").GetGetMethod());
				}, keyValueTypes[1]);
				return;
			}
			GenerateWriteFields(putValueToWriteOnTop, formalType);
		}

		private void GenerateWriteReference(Action<ILGenerator> putValueToWriteOnTop, Type formalType)
		{
			ObjectWriter baseWriter = null; // TODO: fake, maybe promote to field
			PrimitiveWriter primitiveWriter = null; // TODO: as above
			object nullObject = null; // TODO: as above

			var finish = generator.DefineLabel();

			putValueToWriteOnTop(generator);
			var isNotNull = generator.DefineLabel();
			generator.Emit(OpCodes.Brtrue_S, isNotNull);
			generator.Emit(OpCodes.Ldarg_1); // primitiveWriter
			generator.Emit(OpCodes.Ldc_I4_M1); // TODO: Consts value
			generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => primitiveWriter.Write(0)));
			generator.Emit(OpCodes.Br, finish);
			generator.MarkLabel(isNotNull);

			var formalTypeIsActualType = formalType.Attributes.HasFlag(TypeAttributes.Sealed); // TODO: more optimizations?
			// maybe we know the type id yet?
			if(formalTypeIsActualType && typeIndices.ContainsKey(formalType))
			{
				var typeId = typeIndices[formalType];
				generator.Emit(OpCodes.Ldarg_1); // primitiveWriter
				generator.Emit(OpCodes.Ldc_I4, typeId);
				generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => primitiveWriter.Write(0))); // TODO: get it once
			}
			else
			{
				// we have to get the actual type at runtime
				generator.Emit(OpCodes.Ldarg_0); // objectWriter
				putValueToWriteOnTop(generator);
				generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => baseWriter.TouchAndWriteTypeId(nullObject))); // TODO: better do type to type id
			}

			// TODO: other opts here?
			// if there is possibity that the target object is transient, we have to check that
			var skipGetId = false;
			var skipTransientCheck = false;
			if(formalTypeIsActualType)
			{
				if(formalType.IsDefined(typeof(TransientAttribute), false))
				{
					skipGetId = true;
				}
				else
				{
					skipTransientCheck = true;
				}
			}

			if(!skipTransientCheck)
			{
				generator.Emit(OpCodes.Ldarg_0); // objectWriter
				putValueToWriteOnTop(generator); // value to serialize
				generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => baseWriter.CheckTransient(nullObject)));
				generator.Emit(OpCodes.Brtrue_S, finish);
			}

			if(!skipGetId)
			{
				// if the formal type is NOT object, then string or array will not be the content of the field
				// TODO: what with the abstract Array type?
				var mayBeInlined = formalType == typeof(object) || Helpers.CanBeCreatedWithDataOnly(formalType);
				generator.Emit(OpCodes.Ldarg_0); // objectWriter
				putValueToWriteOnTop(generator);
				if(mayBeInlined)
				{
					generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => baseWriter.WriteObjectIdPossiblyInline(null)));
				}
				else
				{
					generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => baseWriter.WriteObjectId(null)));
				}
			}
			generator.MarkLabel(finish);
		}

		private readonly IDictionary<Type, int> typeIndices;
		private readonly ILGenerator generator;
		private readonly DynamicMethod dynamicMethod;

		private static int WriteArrayMethodCounter;
		private static readonly Type[] ParameterTypes = new [] { typeof(ObjectWriter), typeof(PrimitiveWriter), typeof(object) };
	}
}

