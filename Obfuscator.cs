#region Copyright (c) 2007 Ryan Williams <drcforbin@gmail.com>
/// <copyright>
/// Copyright (c) 2007 Ryan Williams <drcforbin@gmail.com>
/// 
/// Permission is hereby granted, free of charge, to any person obtaining a copy
/// of this software and associated documentation files (the "Software"), to deal
/// in the Software without restriction, including without limitation the rights
/// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
/// copies of the Software, and to permit persons to whom the Software is
/// furnished to do so, subject to the following conditions:
/// 
/// The above copyright notice and this permission notice shall be included in
/// all copies or substantial portions of the Software.
/// 
/// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
/// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
/// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
/// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
/// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
/// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
/// THE SOFTWARE.
/// </copyright>
#endregion

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Diagnostics;

using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Security;

using System.Security.Principal;
using System.IO;
using System.Security.AccessControl;

namespace Obfuscar
{
	public class Obfuscator
	{
		internal Project project;

		ObfuscationMap map = new ObfuscationMap( );

		// Unique names for type and members
		int uniqueTypeNameIndex = 0;
		int uniqueMemberNameIndex = 0;

		/// <summary>
		/// Creates an obfuscator initialized from a project file.
		/// </summary>
		/// <param name="projfile">Path to project file.</param>
		public Obfuscator( string projfile )
		{
			// open XmlTextReader over xml string stream
			XmlReaderSettings settings = GetReaderSettings( );

			try
			{
				using ( XmlReader reader = XmlTextReader.Create( System.IO.File.OpenRead( projfile ), settings ) )
					LoadFromReader( reader );
			}
			catch ( System.IO.IOException e )
			{
				throw new ApplicationException( "Unable to read specified project file:  " + projfile, e );
			}
		}

		/// <summary>
		/// Creates an obfuscator initialized from a project file.
		/// </summary>
		/// <param name="projfile">Reader for project file.</param>
		public Obfuscator( XmlReader reader )
		{
			LoadFromReader( reader );
		}

		public static Obfuscator CreateFromXml( string xml )
		{
			// open XmlTextReader over xml string stream
			XmlReaderSettings settings = GetReaderSettings( );

			using ( XmlReader reader = XmlTextReader.Create( new System.IO.StringReader( xml ), settings ) )
				return new Obfuscar.Obfuscator( reader );
		}

		static XmlReaderSettings GetReaderSettings( )
		{
			XmlReaderSettings settings = new XmlReaderSettings( );
			settings.IgnoreProcessingInstructions = true;
			settings.IgnoreWhitespace = true;
			settings.XmlResolver = null;
			settings.ProhibitDtd = false;
			return settings;
		}

		internal Project Project { get { return project; } }

        public bool ShouldHideStrings()
        {
            return Project.Settings.HideStrings;
        }

		void LoadFromReader( XmlReader reader )
		{
			project = Project.FromXml( reader );

			// make sure everything looks good
			project.CheckSettings( );
            NameMaker.UseUnicodeChars = project.Settings.UseUnicodeNames;

			Console.WriteLine( "Loading assemblies..." );
            Console.WriteLine("Extra framework folders: ");
            foreach (var lExtraPath in project.ExtraPaths?? new string[0]) Console.WriteLine(lExtraPath + ", ");
			project.LoadAssemblies( );
		}

		/// <summary>
		/// Saves changes made to assemblies to the output path.
		/// </summary>
		public void SaveAssemblies( )
		{
			string outPath = project.Settings.OutPath;

            //copy excluded assemblies
            foreach (AssemblyInfo copyInfo in project.CopyAssemblyList)
            {
                string outName = System.IO.Path.Combine( outPath,
					System.IO.Path.GetFileName( copyInfo.Filename ) );
                AssemblyFactory.SaveAssembly(copyInfo.Definition, outName);
            }

			// save the modified assemblies
			foreach ( AssemblyInfo info in project )
			{
				string outName = System.IO.Path.Combine( outPath,
					System.IO.Path.GetFileName( info.Filename ) );

                if ( project.Settings.RegenerateDebugInfo )
                {
                    foreach ( ModuleDefinition md in info.Definition.Modules )
                    {
                        md.SaveSymbols( System.IO.Path.GetDirectoryName( outName ));
                        md.Image.DebugHeader.FileName = System.IO.Path.ChangeExtension(outName, "pdb");
                    }
                }
				AssemblyFactory.SaveAssembly( info.Definition, outName );
				if ( info.Definition.Name.HasPublicKey )
				{
                    if (project.KeyContainerName != null)
                    {
                        Obfuscator.MsNetSigner.SignAssemblyFromKeyContainer(outName, project.KeyContainerName);
                    }
                    else
                    {
                        StrongName sn = new StrongName(project.KeyValue);
                        sn.Sign(outName);
                    }
				}
			}
		}

		/// <summary>
		/// Saves the name mapping to the output path.
		/// </summary>
		public void SaveMapping( )
		{
			string filename = project.Settings.XmlMapping?
				"Mapping.xml" : "Mapping.txt";

			string logPath = System.IO.Path.Combine( project.Settings.OutPath, filename );

            if (!String.IsNullOrEmpty(project.Settings.LogFilePath))
                logPath = project.Settings.LogFilePath;

			string lPath = Path.GetDirectoryName(logPath);
            if (!String.IsNullOrEmpty(lPath) && !Directory.Exists(lPath))
                Directory.CreateDirectory(lPath);

			using ( System.IO.TextWriter file = System.IO.File.CreateText( logPath ) )
				SaveMapping( file );
		}

		/// <summary>
		/// Saves the name mapping to a text writer.
		/// </summary>
		public void SaveMapping( System.IO.TextWriter writer )
		{
			IMapWriter mapWriter = project.Settings.XmlMapping ?
				(IMapWriter) new XmlMapWriter( writer ) : (IMapWriter) new TextMapWriter( writer );

			mapWriter.WriteMap( map );
		}

		/// <summary>
		/// Returns the obfuscation map for the project.
		/// </summary>
		ObfuscationMap Mapping
		{
			get { return map; }
		}

		/// <summary>
		/// Renames fields in the project.
		/// </summary>
		public void RenameFields( )
		{
			Dictionary<string, NameGroup> nameGroups = new Dictionary<string, NameGroup>( );

			foreach ( AssemblyInfo info in project )
			{
				AssemblyDefinition library = info.Definition;

				// loop through the types
				foreach ( TypeDefinition type in library.MainModule.Types )
				{
					if ( type.FullName == "<Module>" )
						continue;

					TypeKey typeKey = new TypeKey( type );

					if ( ShouldRename( type ) )
					{
						nameGroups.Clear( );

						// rename field, grouping according to signature
						foreach ( FieldDefinition field in type.Fields )
						{
							string sig = field.FieldType.FullName;
							FieldKey fieldKey = new FieldKey( typeKey, sig, field.Name, field.Attributes );

							NameGroup nameGroup = GetNameGroup( nameGroups, sig );

							if(field.IsRuntimeSpecialName && field.Name == "value__") {
								map.UpdateField (fieldKey, ObfuscationStatus.Skipped, "filtered");
								nameGroup.Add (fieldKey.Name);
							} else

							// skip filtered fields
                                if (info.ShouldSkip(fieldKey) || !ShouldObfuscate(field, type))
							{
								map.UpdateField( fieldKey, ObfuscationStatus.Skipped, "filtered" );
								nameGroup.Add( fieldKey.Name );
							}
							else
							{
								string newName;
								if ( project.Settings.ReuseNames )
									newName = nameGroup.GetNext( );
								else
									newName = NameMaker.UniqueName( uniqueMemberNameIndex++ );

								RenameField( info, fieldKey, field, newName );

								nameGroup.Add( newName );
							}
						}
					}
				}
			}
		}

		void RenameField( AssemblyInfo info, FieldKey fieldKey, FieldDefinition field, string newName )
		{
			// find references, rename them, then rename the field itself

			foreach ( AssemblyInfo reference in info.ReferencedBy )
			{
				for ( int i = 0; i < reference.UnrenamedReferences.Count; )
				{
					FieldReference member = reference.UnrenamedReferences[i] as FieldReference;
					if ( member != null )
					{
						if ( fieldKey.Matches( member ) )
						{
							member.Name = newName;
							reference.UnrenamedReferences.RemoveAt( i );

							// since we removed one, continue without the increment
							continue;
						}
					}

					i++;
				}
			}

			field.Name = newName;

			map.UpdateField( fieldKey, ObfuscationStatus.Renamed, newName );
		}

		/// <summary>
		/// Renames constructor, method, and generic parameters.
		/// </summary>
		public void RenameParams( )
		{
			int index;

			foreach ( AssemblyInfo info in project )
			{
				AssemblyDefinition library = info.Definition;

				// loop through the types
				foreach ( TypeDefinition type in library.MainModule.Types )
				{
					if ( type.FullName == "<Module>" )
						continue;

					if ( ShouldRename( type ) )
					{
						if (info.ShouldSkip(new TypeKey(type)))
							continue;
						System.Reflection.ObfuscationAttribute at = GetObfuscationAttribute (type);
						if(at != null && at.Exclude) continue;

						// rename the constructor parameters
						foreach ( MethodDefinition method in type.Constructors )
							RenameParams( method );

						// rename the method parameters
						foreach ( MethodDefinition method in type.Methods )
							RenameParams( method );

						// rename the class parameters
						index = 0;
						foreach ( GenericParameter param in type.GenericParameters )
							param.Name = NameMaker.UniqueName( index++ );
					}
				}
			}
		}

		void RenameParams( MethodDefinition method )
		{
			int index = 0;
			if(!ShouldObfuscate (method, method.DeclaringType)) return;
			foreach ( ParameterReference param in method.Parameters )
				param.Name = NameMaker.UniqueName( index++ );

			index = 0;
			foreach ( GenericParameter param in method.GenericParameters )
				param.Name = NameMaker.UniqueName( index++ );
		}

		bool ShouldRename( TypeDefinition type )
		{
			const string ctor = "System.Void Obfuscar.ObfuscateAttribute::.ctor()";

			bool should = !project.Settings.MarkedOnly;

			foreach ( CustomAttribute attr in type.CustomAttributes )
			{
				if ( attr.Constructor.ToString( ) == ctor )
				{
					// determine the result from the property, default to true if missing
					object obj = attr.Properties["ShouldObfuscate"];
					if ( obj != null )
						should = (bool) obj;
					else
						should = true;

					break;
				}
			}

			return should;
		}

		/// <summary>
		/// Renames types and resources in the project.
		/// </summary>
		public void RenameTypes( )
		{
			//Dictionary<string, string> typerenamemap = new Dictionary<string, string>(); // For patching the parameters of typeof(xx) attribute constructors

			foreach ( AssemblyInfo info in project )
			{
                Dictionary<string, string> typerenamemap = new Dictionary<string, string>(); // For patching the parameters of typeof(xx) attribute constructors
				AssemblyDefinition library = info.Definition;

				// make a list of the resources that can be renamed
				List<Resource> resources = new List<Resource>( library.MainModule.Resources.Count );
				foreach ( Resource res in library.MainModule.Resources )
					resources.Add( res );

				// Save the original names of all types because parent (declaring) types of nested types may be already renamed.
				// The names are used for the mappings file.
				Dictionary<TypeDefinition, TypeKey> unrenamedTypeKeys = new Dictionary<TypeDefinition, TypeKey>( );
				foreach ( TypeDefinition type in library.MainModule.Types )
					unrenamedTypeKeys.Add( type, new TypeKey( type ) );

				// loop through the types
				int typeIndex = 0;
				foreach ( TypeDefinition type in library.MainModule.Types )
				{
					if ( type.FullName == "<Module>" )
						continue;

					TypeKey oldTypeKey = new TypeKey( type );
					TypeKey unrenamedTypeKey = unrenamedTypeKeys[type];
					string fullName = type.FullName;

					System.Reflection.ObfuscationAttribute atr = GetObfuscationAttribute (type);
					if(ShouldRename (type) && (atr == null || !atr.Exclude)) {
						if(!info.ShouldSkip (unrenamedTypeKey)) {			
							string name;
							string ns;
							if ( project.Settings.ReuseNames )
							{
                                    name = NameMaker.UniqueTypeName( typeIndex );
								ns = NameMaker.UniqueNamespace( typeIndex );
							}
							else
							{
								name = NameMaker.UniqueName( uniqueTypeNameIndex );
                                    ns = NameMaker.UniqueNamespace(uniqueTypeNameIndex);
								uniqueTypeNameIndex++;
							}

							if (type.GenericParameters.Count > 0)
								name += '`' + type.GenericParameters.Count.ToString();
							if (type.DeclaringType != null) // Nested types do not have namespaces
								ns = "";

							TypeKey newTypeKey = new TypeKey( info.Name, ns, name );
							typeIndex++;

							// go through the list of renamed types and try to rename resources
							for ( int i = 0; i < resources.Count; )
							{
								Resource res = resources[i];
								string resName = res.Name;

								if ( resName.StartsWith( fullName + "." ) )
								{
									// If one of the type's methods return a ResourceManager and contains a string with the full type name,
									// we replace the type string with the obfuscated one.
									// This is for the Visual Studio generated resource designer code.
									foreach (MethodDefinition method in type.Methods)
									{
										if (method.ReturnType.ReturnType.FullName == "System.Resources.ResourceManager")
										{
											for (int j = 0; j < method.Body.Instructions.Count; j++)
											{
												Instruction instruction = method.Body.Instructions[j];
												if (instruction.OpCode == OpCodes.Ldstr && (string)instruction.Operand == fullName)
													instruction.Operand = newTypeKey.Fullname;
											}
										}
									}

									string suffix = resName.Substring( fullName.Length );
									string newName = newTypeKey.Fullname + suffix;

									res.Name = newName;
									resources.RemoveAt( i );

									map.AddResource( resName, ObfuscationStatus.Renamed, newName );
								}
								else
									i++;
							}

							RenameType( info, type, oldTypeKey, newTypeKey, unrenamedTypeKey );

							typerenamemap.Add(unrenamedTypeKey.Fullname.Replace('/', '+'), type.FullName.Replace('/', '+'));
						}
						else
						{
							map.UpdateType( oldTypeKey, ObfuscationStatus.Skipped, "filtered" );

							// go through the list of resources, remove ones that would be renamed
							for ( int i = 0; i < resources.Count; )
							{
								Resource res = resources[i];
								string resName = res.Name;

								if ( resName.StartsWith( fullName + "." ) )
								{
									resources.RemoveAt( i );
									map.AddResource( resName, ObfuscationStatus.Skipped, "filtered" );
								}
								else
									i++;
							}
						}
					}
					else
					{
						map.UpdateType( oldTypeKey, ObfuscationStatus.Skipped, "marked" );

						// go through the list of resources, remove ones that would be renamed
						for ( int i = 0; i < resources.Count; )
						{
							Resource res = resources[i];
							string resName = res.Name;

							if ( resName.StartsWith( fullName + "." ) )
							{
								resources.RemoveAt( i );
								map.AddResource( resName, ObfuscationStatus.Skipped, "marked" );
							}
							else
								i++;
						}
					}
				}

				foreach ( Resource res in resources )
					map.AddResource( res.Name, ObfuscationStatus.Skipped, "no clear new name" );

                PatchCustomAttributes(typerenamemap, info);
			}

			//PatchCustomAttributes(typerenamemap);
		}

		void RenameType( AssemblyInfo info, TypeDefinition type, TypeKey oldTypeKey, TypeKey newTypeKey, TypeKey unrenamedTypeKey )
		{
			// find references, rename them, then rename the type itself

			foreach ( AssemblyInfo reference in info.ReferencedBy )
			{
				for ( int i = 0; i < reference.UnrenamedTypeReferences.Count; )
				{
					TypeReference refType = reference.UnrenamedTypeReferences[i];

					// check whether the referencing module references this type...if so,
					// rename the reference
					if ( oldTypeKey.Matches( refType ) )
					{
						refType.Namespace = newTypeKey.Namespace;
						refType.Name = newTypeKey.Name;

						reference.UnrenamedTypeReferences.RemoveAt( i );

						// since we removed one, continue without the increment
						continue;
					}

					i++;
				}
			}

			type.Namespace = newTypeKey.Namespace;
			type.Name = newTypeKey.Name;

			map.UpdateType( unrenamedTypeKey, ObfuscationStatus.Renamed, string.Format("[{0}]{1}", newTypeKey.Scope, type.ToString( )) );
		}

		void PatchCustomAttributes(Dictionary<string, string> typeRenameMap, AssemblyInfo aAssemblyInfo)
		{
			//foreach (AssemblyInfo info in project)
			//{
				//AssemblyDefinition library = info.Definition;
                AssemblyDefinition library = aAssemblyInfo.Definition;

				foreach (TypeDefinition type in library.MainModule.Types)
				{
					PatchCustomAttributeCollection(type.CustomAttributes, typeRenameMap);
					foreach (MethodDefinition methoddefinition in type.Methods)
						PatchCustomAttributeCollection(methoddefinition.CustomAttributes, typeRenameMap);
					foreach (PropertyDefinition propertydefinition in type.Properties)
						PatchCustomAttributeCollection(propertydefinition.CustomAttributes, typeRenameMap);
					foreach (FieldDefinition fielddefinition in type.Fields)
						PatchCustomAttributeCollection(fielddefinition.CustomAttributes, typeRenameMap);
					foreach (EventDefinition eventdefinition in type.Events)
						PatchCustomAttributeCollection(eventdefinition.CustomAttributes, typeRenameMap);
				}
			//}
		}

		void PatchCustomAttributeCollection(CustomAttributeCollection customAttributes, IDictionary<string, string> typeRenameMap)
		{
			foreach (CustomAttribute customattribute in customAttributes)
			{
                customattribute.Resolve();
				for (int i = 0; i < customattribute.Constructor.Parameters.Count; i++)
				{
					ParameterDefinition parameterdefinition = customattribute.Constructor.Parameters[i];
					if (parameterdefinition.ParameterType.FullName == "System.Type")
						customattribute.ConstructorParameters[i] = GetObfuscatedTypeName((string)customattribute.ConstructorParameters[i], typeRenameMap);
				}
				foreach (System.Collections.DictionaryEntry property in new System.Collections.ArrayList(customattribute.Properties))
				{
					if (customattribute.GetPropertyType((string)property.Key).FullName == "System.Type")
						customattribute.Properties[property.Key] = GetObfuscatedTypeName((string)customattribute.Properties[property.Key], typeRenameMap);
				}
				foreach (System.Collections.DictionaryEntry field in new System.Collections.ArrayList(customattribute.Fields))
				{
					if (customattribute.GetPropertyType((string)field.Key).FullName == "System.Type")
						customattribute.Properties[field.Key] = GetObfuscatedTypeName((string)customattribute.Properties[field.Key], typeRenameMap);
				}
			}
		}

		string GetObfuscatedTypeName(string typeString, IDictionary<string, string> typeRenameMap)
		{
			string[] typeparts = typeString.Split(new char[] { ',' });
			if (typeparts.Length > 0) // be paranoid
			{
				string typename = typeparts[0].Trim();
				string obfuscatedtypename;
				if (typeRenameMap.TryGetValue(typename, out obfuscatedtypename))
                {
                    string newtypename = obfuscatedtypename;
                    for (int n = 1; n < typeparts.Length; n++)
                        newtypename += ',' + typeparts[n];
                    return newtypename;
                }
			}
			return typeString;
		}

		Dictionary<ParamSig, NameGroup> GetSigNames( Dictionary<TypeKey, Dictionary<ParamSig, NameGroup>> baseSigNames,
			TypeKey typeKey )
		{
			Dictionary<ParamSig, NameGroup> sigNames;
			if ( !baseSigNames.TryGetValue( typeKey, out sigNames ) )
			{
				sigNames = new Dictionary<ParamSig, NameGroup>( );
				baseSigNames[typeKey] = sigNames;
			}
			return sigNames;
		}

		NameGroup GetNameGroup( Dictionary<TypeKey, Dictionary<ParamSig, NameGroup>> baseSigNames,
			TypeKey typeKey, ParamSig sig )
		{
			return GetNameGroup( GetSigNames( baseSigNames, typeKey ), sig );
		}


		NameGroup GetNameGroup<KeyType>( Dictionary<KeyType, NameGroup> sigNames, KeyType sig )
		{
			NameGroup nameGroup;
			if ( !sigNames.TryGetValue( sig, out nameGroup ) )
			{
				nameGroup = new NameGroup( );
				sigNames[sig] = nameGroup;
			}
			return nameGroup;
		}

		public void RenameProperties( )
		{
			// do nothing if it was requested not to rename
			if ( !project.Settings.RenameProperties )
				return;

			foreach ( AssemblyInfo info in project )
			{
				AssemblyDefinition library = info.Definition;

				foreach ( TypeDefinition type in library.MainModule.Types )
				{
					if ( type.FullName == "<Module>" )
						continue;

					TypeKey typeKey = new TypeKey( type );

					if ( ShouldRename( type ) )
					{
						int index = 0;
						List<PropertyDefinition> propsToDrop = new List<PropertyDefinition>( );
						foreach ( PropertyDefinition prop in type.Properties )
						{
							PropertyKey propKey = new PropertyKey( typeKey, prop );
							ObfuscatedThing m = map.GetProperty( propKey );

							// skip runtime special properties
							if ( prop.IsRuntimeSpecialName )
							{
								m.Update( ObfuscationStatus.Skipped, "runtime special" );
								continue;
							}

							// skip filtered props
							if(info.ShouldSkip (propKey) || !ShouldObfuscate(prop, type) ) {
								m.Update( ObfuscationStatus.Skipped, "filtered" );

								// make sure get/set get skipped too
								if ( prop.GetMethod != null )
									info.ForceSkip( new MethodKey( prop.GetMethod ) );
								if ( prop.SetMethod != null )
									info.ForceSkip( new MethodKey( prop.SetMethod ) );

								continue;
							}
							// do not rename properties of custom attribute types which have a public setter method
							else if ( type.BaseType != null && type.BaseType.Name.EndsWith("Attribute") && prop.SetMethod != null && (prop.SetMethod.Attributes & MethodAttributes.Public) != 0 )
							{
								m.Update( ObfuscationStatus.Skipped, "public setter of a custom attribute" );
								// no problem when the getter or setter methods are renamed by RenameMethods()
							}
							// If a property has custom attributes we don't remove the property but rename it instead.
							else if ( prop.CustomAttributes.Count > 0 )
							{
								string newName;
								if ( project.Settings.ReuseNames )
									newName = NameMaker.UniqueName( index++ );
								else
									newName = NameMaker.UniqueName( uniqueMemberNameIndex++ );
								RenameProperty( info, propKey, prop, newName );
							}
							else
							{
								// add to to collection for removal
								propsToDrop.Add(prop);
							}
						}

						foreach ( PropertyDefinition prop in propsToDrop )
						{
							PropertyKey propKey = new PropertyKey( typeKey, prop );
							ObfuscatedThing m = map.GetProperty( propKey );

							m.Update( ObfuscationStatus.Renamed, "dropped" );
							type.Properties.Remove( prop );
						}
					}
				}
			}
		}

		void RenameProperty( AssemblyInfo info, PropertyKey propertyKey, PropertyDefinition property, string newName )
		{
			// find references, rename them, then rename the property itself

			foreach ( AssemblyInfo reference in info.ReferencedBy )
			{
				for ( int i = 0; i < reference.UnrenamedReferences.Count; )
				{
					PropertyReference member = reference.UnrenamedReferences[i] as PropertyReference;
					if ( member != null )
					{
						if ( propertyKey.Matches( member ) )
						{
							member.Name = newName;
							reference.UnrenamedReferences.RemoveAt( i );

							// since we removed one, continue without the increment
							continue;
						}
					}

					i++;
				}
			}

			property.Name = newName;

			map.UpdateProperty( propertyKey, ObfuscationStatus.Renamed, newName );
		}

		public void RenameEvents( )
		{
			// do nothing if it was requested not to rename
			if ( !project.Settings.RenameEvents )
				return;

			foreach ( AssemblyInfo info in project )
			{
				AssemblyDefinition library = info.Definition;

				foreach ( TypeDefinition type in library.MainModule.Types )
				{
					if ( type.FullName == "<Module>" )
						continue;

					TypeKey typeKey = new TypeKey( type );

					if ( ShouldRename( type ) )
					{
						List<EventDefinition> evtsToDrop = new List<EventDefinition>( );
						foreach ( EventDefinition evt in type.Events )
						{
							EventKey evtKey = new EventKey( typeKey, evt );
							ObfuscatedThing m = map.GetEvent( evtKey );

							// skip runtime special events
							if ( evt.IsRuntimeSpecialName )
							{
								m.Update( ObfuscationStatus.Skipped, "runtime special" );
								continue;
							}

							// skip filtered events
							if(info.ShouldSkip (evtKey) || !ShouldObfuscate(evt, type)) {
								m.Update( ObfuscationStatus.Skipped, "filtered" );

								// make sure add/remove get skipped too
								info.ForceSkip( new MethodKey( evt.AddMethod ) );
								info.ForceSkip( new MethodKey( evt.RemoveMethod ) );

								continue;
							}

							// add to to collection for removal
							evtsToDrop.Add( evt );
						}

						foreach ( EventDefinition evt in evtsToDrop )
						{
							EventKey evtKey = new EventKey( typeKey, evt );
							ObfuscatedThing m = map.GetEvent( evtKey );

							m.Update( ObfuscationStatus.Renamed, "dropped" );
							type.Events.Remove( evt );
						}
					}
				}
			}
		}

		public void RenameMethods( )
		{
			Dictionary<TypeKey, Dictionary<ParamSig, NameGroup>> baseSigNames = 
				new Dictionary<TypeKey, Dictionary<ParamSig, NameGroup>>( );

			foreach ( AssemblyInfo info in project )
			{
				AssemblyDefinition library = info.Definition;

				foreach ( TypeDefinition type in library.MainModule.Types )
				{
					if ( type.FullName == "<Module>" )
						continue;

					TypeKey typeKey = new TypeKey( type );

					Dictionary<ParamSig, NameGroup> sigNames = GetSigNames( baseSigNames, typeKey );

					// first pass.  mark grouped virtual methods to be renamed, and mark some things
					// to be skipped as neccessary
					foreach ( MethodDefinition method in type.Methods )
					{
						string skiprename = null;
						if (!ShouldRename(type))
							skiprename = "Obfuscar.ObfuscateAttribute found on type.";

						MethodKey methodKey = new MethodKey(typeKey, method);
						ObfuscatedThing m = map.GetMethod( methodKey );

						// skip runtime methods
						if ( method.IsRuntime )
							skiprename = "runtime method";

						// skip filtered methods
						if(info.ShouldSkip (methodKey) || !ShouldObfuscate(method, type))
							skiprename = "filtered";

						// update status for skipped non-virtual methods immediately...status for
						// skipped virtual methods gets updated in RenameVirtualMethod
						if ( !method.IsVirtual )
						{
							if (skiprename != null)
								m.Update(ObfuscationStatus.Skipped, skiprename);
							continue;
						}
 
						if ( method.IsSpecialName )
						{
							switch ( method.SemanticsAttributes )
							{
								case MethodSemanticsAttributes.Getter:
								case MethodSemanticsAttributes.Setter:
									if (!project.Settings.RenameProperties)
										skiprename = "skipping properties";
									break;
								case MethodSemanticsAttributes.AddOn:
								case MethodSemanticsAttributes.RemoveOn:
									if (!project.Settings.RenameEvents )
										skiprename="skipping events";
									break;
								default:
									skiprename = "virtual and special name";
									break;
							}
						}

						// if we need to skip the method or we don't yet have a name planned for a method, rename it
						if ( ( skiprename != null && m.Status != ObfuscationStatus.Skipped ) ||
							m.Status == ObfuscationStatus.Unknown )
							RenameVirtualMethod( info, baseSigNames, sigNames, methodKey, method, skiprename );
					}

					// update name groups, so new names don't step on inherited ones
					foreach ( TypeKey baseType in project.InheritMap.GetBaseTypes( typeKey ) )
					{
						Dictionary<ParamSig, NameGroup> baseNames = GetSigNames( baseSigNames, baseType );
						foreach ( KeyValuePair<ParamSig, NameGroup> pair in baseNames )
						{
							NameGroup nameGroup = GetNameGroup( sigNames, pair.Key );
							nameGroup.AddAll( pair.Value );
						}
					}
				}


				foreach ( TypeDefinition type in library.MainModule.Types )
				{
					if ( type.FullName == "<Module>" )
						continue;

					TypeKey typeKey = new TypeKey( type );

					Dictionary<ParamSig, NameGroup> sigNames = GetSigNames( baseSigNames, typeKey );
					// second pass...marked virtuals and anything not skipped get renamed
					foreach ( MethodDefinition method in type.Methods )
					{
						MethodKey methodKey = new MethodKey( typeKey, method );
						ObfuscatedThing m = map.GetMethod( methodKey );

						// if we already decided to skip it, leave it alone
						if ( m.Status == ObfuscationStatus.Skipped )
							continue;

						if ( method.IsSpecialName )
						{
							switch ( method.SemanticsAttributes )
							{
								case MethodSemanticsAttributes.Getter:
								case MethodSemanticsAttributes.Setter:
									if ( project.Settings.RenameProperties )
									{
										RenameMethod( info, sigNames, methodKey, method );
										method.SemanticsAttributes = 0;
									}
									else
										m.Update( ObfuscationStatus.Skipped, "skipping properties" );
									break;
								case MethodSemanticsAttributes.AddOn:
								case MethodSemanticsAttributes.RemoveOn:
									if ( project.Settings.RenameEvents )
									{
										RenameMethod( info, sigNames, methodKey, method );
										method.SemanticsAttributes = 0;
									}
									else
										m.Update( ObfuscationStatus.Skipped, "skipping events" );
									break;
								default:
									m.Update( ObfuscationStatus.Skipped, "special name" );
									break;
							}
						}
						else
							RenameMethod( info, sigNames, methodKey, method );
					}
				}
			}
		}

		void RenameVirtualMethod( AssemblyInfo info, Dictionary<TypeKey, Dictionary<ParamSig, NameGroup>> baseSigNames,
			Dictionary<ParamSig, NameGroup> sigNames, MethodKey methodKey, MethodDefinition method, string skipRename )
		{
			// if method is in a group, look for group key
			MethodGroup group = project.InheritMap.GetMethodGroup( methodKey );
			if ( group != null )
			{
				string groupName = group.Name;
				if ( groupName == null )
				{
					// group is not yet named

					// counts are grouping according to signature
					ParamSig sig = new ParamSig( method );

					// get name groups for classes in the group
					NameGroup[] nameGroups = GetNameGroups( baseSigNames, group.Methods, sig );

					if ( group.External )
						skipRename = "external base class or interface";
					if ( skipRename != null )
					{
						// for an external group, we can't rename.  just use the method 
						// name as group name
						groupName = method.Name;
					}
					else
					{
						// for an internal group, get next unused name
						groupName = NameGroup.GetNext( nameGroups );
					}

					group.Name = groupName;

					// set up methods to be renamed
					foreach ( MethodKey m in group.Methods )
						if (skipRename == null)
							map.UpdateMethod(m, ObfuscationStatus.WillRename, groupName);
						else
							map.UpdateMethod(m, ObfuscationStatus.Skipped, skipRename);

					// make sure the classes' name groups are updated
					for ( int i = 0; i < nameGroups.Length; i ++ )
						nameGroups[i].Add( groupName );
				}
				else if ( skipRename != null )
				{
					// group is named, so we need to un-name it

					Debug.Assert( !group.External,
						"Group's external flag should have been handled when the group was created, " +
						"and all methods in the group should already be marked skipped." );

					// counts are grouping according to signature
					ParamSig sig = new ParamSig( method );

					// get name groups for classes in the group
					NameGroup[] nameGroups = GetNameGroups( baseSigNames, group.Methods, sig );

					// make sure to remove the old group name from the classes' name groups
					for ( int i = 0; i < nameGroups.Length; i++ )
						nameGroups[i].Remove( groupName );

					// since this method has to be skipped, we need to use the method 
					// name as new group name
					groupName = method.Name;
					group.Name = groupName;

					// set up methods to be renamed
					foreach ( MethodKey m in group.Methods )
						map.UpdateMethod( m, ObfuscationStatus.Skipped, skipRename );

					// make sure the classes' name groups are updated
					for ( int i = 0; i < nameGroups.Length; i++ )
						nameGroups[i].Add( groupName );
				}
				else
				{
					ObfuscatedThing m = map.GetMethod( methodKey );
					Debug.Assert( m.Status == ObfuscationStatus.Skipped || 
						( ( m.Status == ObfuscationStatus.WillRename || m.Status == ObfuscationStatus.Renamed ) &&
						m.StatusText == groupName ),
						"If the method isn't skipped, and the group already has a name...method should have one too." );
				}
			}
			else if (skipRename != null)
				map.UpdateMethod(methodKey, ObfuscationStatus.Skipped, skipRename);
		}

		NameGroup[] GetNameGroups( Dictionary<TypeKey, Dictionary<ParamSig, NameGroup>> baseSigNames,
			IEnumerable<MethodKey> methodKeys, ParamSig sig )
		{
			// build unique set of classes in group
			C5.HashSet<TypeKey> typeKeys = new C5.HashSet<TypeKey>( );
			foreach ( MethodKey methodKey in methodKeys )
				typeKeys.Add( methodKey.TypeKey );

			// build list of namegroups
			NameGroup[] nameGroups = new NameGroup[typeKeys.Count];

			int i = 0;
			foreach ( TypeKey typeKey in typeKeys )
			{
				NameGroup nameGroup = GetNameGroup( baseSigNames, typeKey, sig );

				nameGroups[i++] = nameGroup;
			}

			return nameGroups;
		}

		string GetNewMethodName( Dictionary<ParamSig, NameGroup> sigNames, MethodKey methodKey, MethodDefinition method )
		{
			ObfuscatedThing t = map.GetMethod( methodKey );

			// if it already has a name, return it
			if ( t.Status == ObfuscationStatus.Renamed ||
				t.Status == ObfuscationStatus.WillRename )
				return t.StatusText;

			// don't mess with methods we decided to skip
			if ( t.Status == ObfuscationStatus.Skipped )
				return null;

			// counts are grouping according to signature
			ParamSig sig = new ParamSig( method );

			NameGroup nameGroup = GetNameGroup( sigNames, sig );

			string newName = nameGroup.GetNext( );

			// got a new name for the method
			t.Status = ObfuscationStatus.WillRename;
			t.StatusText = newName;

			// make sure the name groups is updated
			nameGroup.Add( newName );

			return newName;
		}

		void RenameMethod( AssemblyInfo info, Dictionary<ParamSig, NameGroup> sigNames, MethodKey methodKey, MethodDefinition method )
		{
			string newName = GetNewMethodName( sigNames, methodKey, method );

			RenameMethod( info, methodKey, method, newName );
		}

		void RenameMethod( AssemblyInfo info, MethodKey methodKey, MethodDefinition method, string newName )
		{
			// find references, rename them, then rename the method itself
			foreach ( AssemblyInfo reference in info.ReferencedBy )
			{
				for ( int i = 0; i < reference.UnrenamedReferences.Count; )
				{
					MethodReference member = reference.UnrenamedReferences[i] as MethodReference;
					if ( member != null )
					{
						if ( methodKey.Matches( member ) )
						{
							member.Name = newName;
							reference.UnrenamedReferences.RemoveAt( i );

							// since we removed one, continue without the increment
							continue;
						}
					}

					i++;
				}
			}

			method.Name = newName;

			map.UpdateMethod( methodKey, ObfuscationStatus.Renamed, newName );
		}

		private bool ShouldObfuscate (ICustomAttributeProvider member, TypeDefinition def)
		{
			System.Reflection.ObfuscationAttribute at = GetObfuscationAttribute (member);
			if(at != null) {
				if(at.Exclude) return false;
			}
			at = GetObfuscationAttribute (def);
			if(at != null) {
				if(at.Exclude && at.ApplyToMembers)
					return false;
			}
			return true;
		}

		private System.Reflection.ObfuscationAttribute GetObfuscationAttribute (ICustomAttributeProvider attributes)
		{
			IAnnotationProvider ap = attributes as IAnnotationProvider;
			if(ap != null && ap.Annotations ["Obfuscation"] != null)
				return (System.Reflection.ObfuscationAttribute)ap.Annotations ["Obfuscation"];
			if (attributes == null || !attributes.HasCustomAttributes) return null;
			for (int i = 0; i < attributes.CustomAttributes.Count; i++)
			{
				CustomAttribute at = attributes.CustomAttributes [i];
				if(at.Constructor.DeclaringType.FullName == "System.Reflection.ObfuscationAttribute") {
					at.Resolve ();
					System.Reflection.ObfuscationAttribute res = new System.Reflection.ObfuscationAttribute ();
					if(at.Properties ["ApplyToMembers"] is bool)
						res.ApplyToMembers = (bool)at.Properties ["ApplyToMembers"];
					if(at.Properties ["Exclude"] is bool)
						res.Exclude = (bool)at.Properties ["Exclude"];
					if(at.Properties ["StripAfterObfuscation"] is bool)
						res.StripAfterObfuscation = (bool)at.Properties ["StripAfterObfuscation"];
					if(at.Properties ["Feature"] is string)
						res.Feature = (string)at.Properties ["Feature"];

					if(res.StripAfterObfuscation)
						attributes.CustomAttributes.RemoveAt (i);
					if(ap != null)
						ap.Annotations ["Obfuscation"] = res;
					return res;
				}
			}
			return null;
		}


        private MethodReference CreateMethodReference(Type lType, System.Reflection.MethodInfo lMethodInfo, ModuleDefinition lModuleDefinition, AssemblyNameReference lmscorlib)
        {
            TypeReference lDeclaringType = CreateTypeReference(lType, lModuleDefinition, lmscorlib);
            TypeReference lReturnType = CreateTypeReference(lMethodInfo.ReturnType, lModuleDefinition, lmscorlib);

            MethodReference lMethod = new MethodReference(lMethodInfo.Name, lDeclaringType, lReturnType, (lMethodInfo.CallingConvention & System.Reflection.CallingConventions.HasThis) > 0, (lMethodInfo.CallingConvention & System.Reflection.CallingConventions.ExplicitThis) > 0, MethodCallingConvention.Default);
            lModuleDefinition.MemberReferences.Add(lMethod);

            System.Reflection.ParameterInfo[] lParameters = lMethodInfo.GetParameters();
            if (lParameters != null)
            {
                foreach (System.Reflection.ParameterInfo lParam in lParameters)
                {
                    var lParameterType = CreateTypeReference(lParam.ParameterType, lModuleDefinition, lmscorlib);
                    var lParamDefinition = new ParameterDefinition(lParam.Name, -1, Mono.Cecil.ParameterAttributes.None, lParameterType);
                    lMethod.Parameters.Add(lParamDefinition);
                }
            }
            return lMethod;
        }

        TypeReference CreateTypeReference(Type lType, ModuleDefinition lModuleDefinition, AssemblyNameReference lmscorlib)
        {
            TypeReference lTypeReference = new TypeReference(lType.Name, lType.Namespace, lmscorlib, lType.IsValueType);
            lModuleDefinition.TypeReferences.Add(lTypeReference);

            return lTypeReference;
        }


		public void HideStrings( )
		{
			foreach ( AssemblyInfo info in project )
			{
				AssemblyDefinition library = info.Definition;

				Dictionary<string, MethodDefinition> methodByString = new Dictionary<string, MethodDefinition>( );

				int nameIndex = 0;

				// We get the most used type references
				TypeReference systemObjectTypeReference = library.MainModule.Import( typeof( Object ) );
				TypeReference systemVoidTypeReference = library.MainModule.Import( typeof( void ) );
				TypeReference systemStringTypeReference = library.MainModule.Import( typeof( String ) );

                //check if mscorlib there and import types only if it is not there
                AssemblyNameReference lmscorlibRef = null; //typeof(byte).Assembly;
                foreach (AssemblyNameReference lRef in library.MainModule.AssemblyReferences)
                {
                    if (lRef.Name == "mscorlib")
                    {
                        lmscorlibRef = lRef;
                    }
                }
                lmscorlibRef = null;

                TypeReference systemValueTypeTypeReference = null;
                TypeReference systemByteTypeReference = null;
                TypeReference systemIntTypeReference = null;
                if (lmscorlibRef == null)
                {
                    systemValueTypeTypeReference = library.MainModule.Import(typeof(ValueType));
                    systemByteTypeReference = library.MainModule.Import(typeof(byte));
                    systemIntTypeReference = library.MainModule.Import(typeof(int));
                }
                else
                {
                    systemValueTypeTypeReference = CreateTypeReference(typeof(ValueType), library.MainModule, lmscorlibRef);
                    systemByteTypeReference = CreateTypeReference(typeof(byte), library.MainModule, lmscorlibRef);
                    systemIntTypeReference = CreateTypeReference(typeof(int), library.MainModule, lmscorlibRef);
                }

				// New static class with a method for each unique string we substitute.
				TypeDefinition newtype = new TypeDefinition( "<PrivateImplementationDetails>{" + Guid.NewGuid( ).ToString( ).ToUpper( ) + "}", null, TypeAttributes.BeforeFieldInit | TypeAttributes.AutoClass | TypeAttributes.AnsiClass | TypeAttributes.BeforeFieldInit, systemObjectTypeReference );

				// Array of bytes receiving the obfuscated strings in UTF8 format.
				List<byte> databytes = new List<byte>( );

				// Add struct for constant byte array data
                TypeDefinition structType = new TypeDefinition(NameMaker.UniqueName(1), "", TypeAttributes.ExplicitLayout | TypeAttributes.AnsiClass | TypeAttributes.Sealed | TypeAttributes.NestedPrivate, systemValueTypeTypeReference);
				structType.PackingSize = 1;
				newtype.NestedTypes.Add( structType );

				// Add field with constant string data
                FieldDefinition dataConstantField = new FieldDefinition(NameMaker.UniqueName(1), structType, FieldAttributes.HasFieldRVA | FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.Assembly);
				newtype.Fields.Add( dataConstantField );

				// Add data field where constructor copies the data to
                FieldDefinition dataField = new FieldDefinition(NameMaker.UniqueName(2), new ArrayType(systemByteTypeReference), FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.Assembly);
				newtype.Fields.Add( dataField );

				// Add string array of deobfuscated strings
                FieldDefinition stringArrayField = new FieldDefinition(NameMaker.UniqueName(3), new ArrayType(systemStringTypeReference), FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.Assembly);
				newtype.Fields.Add( stringArrayField );

				// Add method to extract a string from the byte array. It is called by the indiviual string getter methods we add later to the class.
                MethodDefinition stringGetterMethodDefinition = new MethodDefinition(NameMaker.UniqueName(1), MethodAttributes.Static | MethodAttributes.Private | MethodAttributes.HideBySig, systemStringTypeReference);
                stringGetterMethodDefinition.Body.InitLocals = true;
				stringGetterMethodDefinition.Parameters.Add( new ParameterDefinition( systemIntTypeReference ) );
				stringGetterMethodDefinition.Parameters.Add( new ParameterDefinition( systemIntTypeReference ) );
				stringGetterMethodDefinition.Parameters.Add( new ParameterDefinition( systemIntTypeReference ) );
				stringGetterMethodDefinition.Body.Variables.Add( new VariableDefinition( systemStringTypeReference ) );
				CilWorker worker3 = stringGetterMethodDefinition.Body.CilWorker;

                MethodReference lEncodingUtf8 = null;
                MethodReference lEncodingGetString = null;
                if (lmscorlibRef == null)
                {
                    lEncodingUtf8 = library.MainModule.Import(typeof(System.Text.Encoding).GetProperty("UTF8").GetGetMethod());
                    lEncodingGetString = library.MainModule.Import(typeof(System.Text.Encoding).GetMethod("GetString", new Type[] { typeof(byte[]), typeof(int), typeof(int) }));
                }
                else
                {
                    var lEncodingType = typeof(System.Text.Encoding);
                    lEncodingUtf8 = CreateMethodReference(lEncodingType, lEncodingType.GetProperty("UTF8").GetGetMethod(), library.MainModule, lmscorlibRef);
                    lEncodingGetString = CreateMethodReference(lEncodingType, lEncodingType.GetMethod("GetString", new Type[] { typeof(byte[]), typeof(int), typeof(int) }), library.MainModule, lmscorlibRef);
                }

                worker3.Emit(OpCodes.Call, lEncodingUtf8/*library.MainModule.Import( typeof( System.Text.Encoding ).GetProperty( "UTF8" ).GetGetMethod( ) )*/ );
				worker3.Emit( OpCodes.Ldsfld, dataField );
				worker3.Emit( OpCodes.Ldarg_1 );
				worker3.Emit( OpCodes.Ldarg_2 );
                worker3.Emit(OpCodes.Callvirt, lEncodingGetString/*library.MainModule.Import( typeof( System.Text.Encoding ).GetMethod( "GetString", new Type[] { typeof( byte[] ), typeof( int ), typeof( int ) } ) ) */);
				worker3.Emit( OpCodes.Stloc_0 );

				worker3.Emit( OpCodes.Ldsfld, stringArrayField );
				worker3.Emit( OpCodes.Ldarg_0 );
				worker3.Emit( OpCodes.Ldloc_0 );
				worker3.Emit( OpCodes.Stelem_Ref );

				worker3.Emit( OpCodes.Ldloc_0 );
				worker3.Emit( OpCodes.Ret );
				newtype.Methods.Add( stringGetterMethodDefinition );

				int stringIndex = 0;

				// Look for all string load operations and replace them with calls to indiviual methods in our new class
				foreach ( TypeDefinition type in library.MainModule.Types )
				{
					if ( type.FullName == "<Module>" )
						continue;

					TypeKey typeKey = new TypeKey( type );
					if ( ShouldRename( type ) )
					{
						foreach ( MethodDefinition method in type.Methods )
						{
							if (!info.ShouldSkipStringHiding(new MethodKey(method)) && method.Body != null )
							{
							    //SequencePoint sp = method.Body.Instructions;
								for ( int i = 0; i < method.Body.Instructions.Count; i++ )
								{
									Instruction instruction = method.Body.Instructions[i];
									if ( instruction.OpCode == OpCodes.Ldstr )
									{
										string str = (string) instruction.Operand;
										MethodDefinition individualStringMethodDefinition = null;
										if ( !methodByString.TryGetValue( str, out individualStringMethodDefinition ) )
										{
											string methodName = NameMaker.UniqueName( nameIndex++ );

											// Add the string to the data array
											byte[] stringBytes = Encoding.UTF8.GetBytes( str );
											int start = databytes.Count;
											databytes.AddRange( stringBytes );
											int count = databytes.Count - start;

											// Add a method for this string to our new class
											individualStringMethodDefinition = new MethodDefinition( methodName, MethodAttributes.Static | MethodAttributes.Public | MethodAttributes.HideBySig, systemStringTypeReference );
											individualStringMethodDefinition.Body = new MethodBody( individualStringMethodDefinition );
											CilWorker worker4 = individualStringMethodDefinition.Body.CilWorker;

											worker4.Emit( OpCodes.Ldsfld, stringArrayField );
											worker4.Emit( OpCodes.Ldc_I4, stringIndex );
											worker4.Emit( OpCodes.Ldelem_Ref );
											worker4.Emit( OpCodes.Dup );
											Instruction label20 = worker4.Emit( OpCodes.Brtrue_S, stringGetterMethodDefinition.Body.Instructions[0] );
											worker4.Emit( OpCodes.Pop );
											worker4.Emit( OpCodes.Ldc_I4, stringIndex );
											worker4.Emit( OpCodes.Ldc_I4, start );
											worker4.Emit( OpCodes.Ldc_I4, count );
											worker4.Emit( OpCodes.Call, stringGetterMethodDefinition );

											label20.Operand = worker4.Emit( OpCodes.Ret );

											newtype.Methods.Add( individualStringMethodDefinition );
											methodByString.Add( str, individualStringMethodDefinition );

											stringIndex++;
										}
										CilWorker worker = method.Body.CilWorker;
										Instruction newinstruction = worker.Create( OpCodes.Call, individualStringMethodDefinition );
                                        newinstruction.SequencePoint = instruction.SequencePoint;
                                        ReplaceScopePoint( method.Body.Scopes, instruction, newinstruction );
										worker.Replace( instruction, newinstruction );
									}
								}
							}
						}
					}
				}

				// Now that we know the total size of the byte array, we can update the struct size and store it in the constant field
				structType.ClassSize = (uint) databytes.Count;
				for ( int i = 0; i < databytes.Count; i++ )
					databytes[i] = (byte) (databytes[i] ^ (byte)i ^ 0xAA);
				dataConstantField.InitialValue = databytes.ToArray( );


				// Add static constructor which initializes the dataField from the constant data field
				MethodDefinition ctorMethodDefinition = new MethodDefinition( ".cctor", MethodAttributes.Static | MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, systemVoidTypeReference );
				newtype.Methods.Add( ctorMethodDefinition );
				ctorMethodDefinition.Body = new MethodBody( ctorMethodDefinition );
				ctorMethodDefinition.Body.Variables.Add( new VariableDefinition( systemIntTypeReference ) );
                ctorMethodDefinition.Body.InitLocals = true;

				CilWorker worker2 = ctorMethodDefinition.Body.CilWorker;
				worker2.Emit( OpCodes.Ldc_I4, stringIndex );
				worker2.Emit( OpCodes.Newarr, systemStringTypeReference );
				worker2.Emit( OpCodes.Stsfld, stringArrayField );

                MethodReference lRuntimeHelpers = null;
                if (lmscorlibRef == null)
                {
                    lRuntimeHelpers = library.MainModule.Import(typeof(System.Runtime.CompilerServices.RuntimeHelpers).GetMethod("InitializeArray"));
                }
                else
                {
                    var lRuntimeHelpersType = typeof(System.Runtime.CompilerServices.RuntimeHelpers);
                    lRuntimeHelpers = CreateMethodReference(lRuntimeHelpersType, typeof(System.Runtime.CompilerServices.RuntimeHelpers).GetMethod("InitializeArray"), library.MainModule, lmscorlibRef);
                }

				worker2.Emit( OpCodes.Ldc_I4, databytes.Count );
				worker2.Emit( OpCodes.Newarr, systemByteTypeReference );
				worker2.Emit( OpCodes.Dup );
				worker2.Emit( OpCodes.Ldtoken, dataConstantField );
                worker2.Emit(OpCodes.Call, lRuntimeHelpers/*library.MainModule.Import( typeof( System.Runtime.CompilerServices.RuntimeHelpers ).GetMethod("InitializeArray"))*/);
				worker2.Emit( OpCodes.Stsfld, dataField );

				worker2.Emit( OpCodes.Ldc_I4_0 );
				worker2.Emit( OpCodes.Stloc_0 );

				Instruction backlabel1 = worker2.Emit( OpCodes.Br_S, ctorMethodDefinition.Body.Instructions[0] );
				Instruction label2 = worker2.Emit( OpCodes.Ldsfld, dataField );
				worker2.Emit( OpCodes.Ldloc_0 );
				worker2.Emit( OpCodes.Ldsfld, dataField );
				worker2.Emit( OpCodes.Ldloc_0 );
				worker2.Emit( OpCodes.Ldelem_U1 );
				worker2.Emit( OpCodes.Ldloc_0 );
				worker2.Emit( OpCodes.Xor );
				worker2.Emit( OpCodes.Ldc_I4, 0xAA );
				worker2.Emit( OpCodes.Xor );
				worker2.Emit( OpCodes.Conv_U1 );
				worker2.Emit( OpCodes.Stelem_I1 );
				worker2.Emit( OpCodes.Ldloc_0 );
				worker2.Emit( OpCodes.Ldc_I4_1 );
				worker2.Emit( OpCodes.Add );
				worker2.Emit( OpCodes.Stloc_0 );
				backlabel1.Operand = worker2.Emit( OpCodes.Ldloc_0 );
				worker2.Emit( OpCodes.Ldsfld, dataField );
				worker2.Emit( OpCodes.Ldlen );
				worker2.Emit( OpCodes.Conv_I4 );
				worker2.Emit( OpCodes.Clt );
				worker2.Emit( OpCodes.Brtrue, label2 );
				worker2.Emit( OpCodes.Ret );


				library.MainModule.Types.Add( structType );
				library.MainModule.Types.Add( newtype );
			}
		}

        private void ReplaceScopePoint(ScopeCollection scopeCollection, Instruction instruction, Instruction newinstruction)
        {
            for (int i = 0; i < scopeCollection.Count; i++)
            {
                if (scopeCollection[i].Start == instruction)
                    scopeCollection[i].Start = newinstruction;
                else if (scopeCollection[i].End == instruction)
                    scopeCollection[i].End = newinstruction;
                if (scopeCollection[i].Scopes != null)
                    ReplaceScopePoint(scopeCollection[i].Scopes, instruction, newinstruction);
            }
        }

        public static class MsNetSigner
        {
            [System.Runtime.InteropServices.DllImport("mscoree.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
            private static extern bool StrongNameSignatureGeneration(
                [/*In, */System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]string wzFilePath,
                [/*In, */System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]string wzKeyContainer,
                /*[In]*/byte[] pbKeyBlob,
                /*[In]*/uint cbKeyBlob,
                /*[In]*/IntPtr ppbSignatureBlob, // not supported, always pass 0.
                [System.Runtime.InteropServices.Out]out uint pcbSignatureBlob
                );

            public static void SignAssemblyFromKeyContainer(string assemblyname, string keyname)
            {
                uint dummy;
                if (!StrongNameSignatureGeneration(assemblyname, keyname, null, 0, IntPtr.Zero, out dummy))
                    throw new Exception("Unable to sign assembly using key from key container - " + keyname);
            }

        //    internal static bool TryKeyContainerPermissionCheck(string secretKeyName)
        //    {

        //        bool returnValue = false;

        //        WindowsIdentity current = WindowsIdentity.GetCurrent();

        //        WindowsPrincipal currentPrincipal = new WindowsPrincipal(current);

        //        if (currentPrincipal.IsInRole(WindowsBuiltInRole.Administrator))
        //        {
        //            try
        //            {
        //                foreach (string fileName in Directory.GetFiles(
        //                    @"C:\Documents and Settings\All Users\" +
        //                    @"Application Data\Microsoft\Crypto\RSA\MachineKeys"))
        //                {
        //                    FileInfo fi = new FileInfo(fileName);

        //                    if (fi.Length <= 1024 * 5)
        //                    { // no key file should be greater then 5KB
        //                        try
        //                        {
        //                            using (StreamReader sr = fi.OpenText())
        //                            {
        //                                string fileData = sr.ReadToEnd();
        //                                if (fileData.Contains(secretKeyName))
        //                                { // this is our file

        //                                    FileSecurity fileSecurity = fi.GetAccessControl();

        //                                    bool currentIdentityFoundInACL = false;
        //                                    foreach (FileSystemAccessRule rule in fileSecurity
        //                                                                               .GetAccessRules(true, true, typeof(NTAccount)))
        //                                    {
        //                                        if (rule.IdentityReference.Value.ToLower()
        //                                            == current.Name.ToLower())
        //                                        {
        //                                            returnValue = true;
        //                                            currentIdentityFoundInACL = true;
        //                                            break;
        //                                        }
        //                                    }

        //                                    //if (!currentIdentityFoundInACL)
        //                                    {
        //                                        fileSecurity.AddAccessRule(
        //                                            new FileSystemAccessRule(
        //                                                current.Name,
        //                                                FileSystemRights.FullControl,
        //                                                AccessControlType.Allow
        //                                            ));

        //                                        fi.SetAccessControl(fileSecurity);

        //                                        returnValue = true;
        //                                    }
        //                                    break;
        //                                }
        //                            }
        //                        }
        //                        catch { }
        //                    }
        //                }
        //            }
        //            catch (UnauthorizedAccessException)
        //            {
        //                throw;
        //            }
        //            catch { }
        //        }

        //        return returnValue;
        //    }
        }
    }
}