﻿using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using IStoryThread = System.Collections.Generic.IEnumerable<Cradle.StoryOutput>;

namespace Cradle
{
	public enum StoryState
	{
		Idle = 0,
		Playing = 1,
		Paused = 2,
		Exiting = 3
	}

	public enum CueType
	{
		Enter,
		Exit,
		Update,
		Output,
		Aborted,
		Done
	}

    public abstract class Story: MonoBehaviour
    {
		public bool AutoPlay = true;
        public string StartPassage = "Start";
		public List<GameObject> AdditionalCues;

		public event Action<StoryPassage> OnPassageEnter;
		public event Action<StoryPassage> OnPassageDone;
		public event Action<StoryPassage> OnPassageExit;
		public event Action<StoryState> OnStateChanged;
		public event Action<StoryPassage, StoryLink> OnLinkEnter;
		public event Action<StoryPassage, StoryLink> OnLinkDone;
		public event Action<StoryOutput> OnOutput;
		public event Action<StoryOutput> OnOutputRemoved;

		public Dictionary<string, StoryPassage> Passages { get; private set; }
		public List<StoryOutput> Output { get; private set; }
		public RuntimeVars Vars { get; protected set; }
		public StoryPassage CurrentPassage { get; private set; }
		public StoryLink CurrentLinkInAction { get; private set; }
		public int NumberOfLinksDone { get; private set; }
        public List<string> PassageHistory {get; private set; }
		public float PassageTime { get { return _timeAccumulated + (Time.time - _timeChangedToPlay); } }
		//public StorySaveData SaveData { get; private set; }
		public bool IsPausable { get { return _allowStateChanging; } }

		bool _allowStateChanging = true;
		StoryState _state = StoryState.Idle;
		readonly CallbackList _callbacks;
		IEnumerator<StoryOutput> _currentThread = null;
		Cue[] _passageUpdateCues = null;
		Dictionary<string, List<Cue>> _cueCache = new Dictionary<string, List<Cue>>();
		MonoBehaviour[] _cueTargets = null;
		float _timeChangedToPlay = 0f;
		float _timeAccumulated;
		Stack<StyleGroup> _styleGroupStack = new Stack<StyleGroup>();

		protected Stack<int> InsertStack = new Stack<int>();

		[Obsolete("Use Story.CurrentPassage.Name instead")]
		public string CurrentPassageName { get { return CurrentPassage.Name; } }

		[Obsolete("Use Story.CurrentPassage.Tags instead")]
		public string[] Tags { get { return CurrentPassage.Tags; } }

		internal class Cue
		{
			public MonoBehaviour target;
			public MethodInfo method;
		}

		private enum ThreadResult
		{
			InProgress = 0,
			Done = 1,
			//Aborted = 2
		}

		public Story()
		{
			StoryVar.RegisterTypeService<bool>(new BoolService());
			StoryVar.RegisterTypeService<int>(new IntService()); 
			StoryVar.RegisterTypeService<double>(new DoubleService());
			StoryVar.RegisterTypeService<string>(new StringService());

			this.Passages = new Dictionary<string, StoryPassage>();

            this.PassageHistory = new List<string>();

			_callbacks = new CallbackList(this);
		}

		protected void Init()
		{
			_state = StoryState.Idle;
			this.Output = new List<StoryOutput>();

			NumberOfLinksDone = 0;
			PassageHistory.Clear();
			InsertStack.Clear();
			
			CurrentPassage = null;
			_callbacks.Reset();
		}

		void Start()
		{
			if (AutoPlay)
				this.Begin();
		}

		// ---------------------------------
		// State control

		public StoryState State
		{
			get { return _state; }
			private set
			{
				StoryState prev = _state;
				_state = value;
				if (prev != value && OnStateChanged != null)
				{
					_allowStateChanging = false;
					OnStateChanged(value);
					_allowStateChanging = true;
				}
			}
		}

		public void Reset()
		{
			if (this.State != StoryState.Idle)
				throw new InvalidOperationException("Can only reset a story that is Idle.");

			// Reset twine vars
			if (Vars != null)
				Vars.Reset();

			this.Init();
		}

		/*
		// Requires further testing
		protected virtual StorySaveData Save()
		{
			var saveData = new StorySaveData()
			{
				PassageHistory = this.PassageHistory.ToList(),
				PassageToResume = this.CurrentPassage.Name,
				Variables = new Dictionary<string, StoryVar>()
			};

			foreach(var pair in this.Vars)
				saveData.Variables[pair.Key] = pair.Value;

			return saveData;
		}

		public virtual void Load(StorySaveData saveData)
		{
			if (this.State != StoryState.Idle)
				throw new InvalidOperationException("Can only load a story that is Idle.");

			this.Reset();
			this.PassageHistory.AddRange(saveData.PassageHistory);
			foreach (var pair in saveData.Variables)
				this.Vars[pair.Key] = pair.Value;

			this.GoTo(saveData.PassageToResume);
		}
		*/

		/// <summary>
		/// Begins the story by calling GoTo(StartPassage).
		/// </summary>
		public void Begin()
		{
			GoTo(StartPassage);
		}

		/// <summary>
		/// 
		/// </summary>
		public void GoTo(string passageName)
		{
			if (this.State != StoryState.Idle)
			{
				throw new InvalidOperationException(
					// Paused
					this.State == StoryState.Paused ?
						"The story is currently paused. Resume() must be called before advancing to a different passage." :
					// Playing
					this.State == StoryState.Playing || this.State == StoryState.Exiting ?
						"The story can only be advanced when it is in the Idle state." :
					// Complete
						"The story is complete. Reset() must be called before it can be played again."
					);
			}

			//===========================

			_callbacks.Reset();

			if (CurrentPassage != null)
			{
				this.State = StoryState.Exiting;

				// Build a callback list for exiting
				_callbacks
					.Add(() =>
					{
						if (this.OnPassageExit != null)
							this.OnPassageExit(this.CurrentPassage);
					})
					.Add(() =>
					{
						SendMessage("OnStoryPassageExit", this.CurrentPassage, SendMessageOptions.DontRequireReceiver);
					})
					.Add(CuesFind(CueType.Exit, reverse: true))
					.Add(() =>
					{
						// Add the passage only now that we're leaving it
						PassageHistory.Add(CurrentPassage.Name);
					})
					.OnComplete(() => Enter(passageName))
					.Invoke();
			}
			else
				Enter(passageName);
		}

		/// <summary>
		/// While the story is playing, pauses the execution of the current story thread.
		/// </summary>
		public void Pause()
		{
			if (!_allowStateChanging)
				throw new InvalidOperationException("Can't pause story right now. Check story.IsPausable.");

			if (this.State != StoryState.Playing && this.State != StoryState.Exiting)
				throw new InvalidOperationException("Pause can only be called while a passage is playing or exiting.");

			this.State = StoryState.Paused;
			_timeAccumulated = Time.time - _timeChangedToPlay;
		}

		/// <summary>
		/// When the story is paused, resumes execution of the current story thread.
		/// </summary>
		public void Resume()
		{
			if (!_allowStateChanging)
				throw new InvalidOperationException("Can't resume story right now. Check story.IsPausable.");

			if (this.State != StoryState.Paused)
			{
				throw new InvalidOperationException(
					// Paused
					this.State == StoryState.Idle ?
						"The story is currently idle. Call Begin, DoLink or GoTo to play." :
					// Playing
					this.State == StoryState.Playing || this.State == StoryState.Exiting?
						"Resume() should be called only when the story is paused." :
					// Complete
						"The story is complete. Reset() must be called before it can be played again."
					);
			}

			this.State = StoryState.Playing;
			_timeAccumulated = Time.time - _timeChangedToPlay;

			// If a callback list is still in progress, keep invoking it
			if (_callbacks != null && !_callbacks.Completed)
				_callbacks.Invoke();

			// If present, continue the current thread as long as a callback didn't pause it
			if (_currentThread != null && this.State == StoryState.Playing)
				ExecuteCurrentThread();
		}

		public StoryPassage GetPassage(string passageName)
		{
			StoryPassage passage;
			if (!Passages.TryGetValue(passageName, out passage))
				throw new StoryException(String.Format("Passage '{0}' does not exist.", passageName));
			return passage;
		}

		public IEnumerable<string> GetPassagesWithTag(string tag)
		{
			return this.Passages
				.Where(pair => pair.Value.Tags.Contains(tag, System.StringComparer.InvariantCultureIgnoreCase))
				.Select(pair => pair.Key)
				.OrderBy(passageName => passageName, StringComparer.InvariantCulture);
		}

		protected virtual Func<IStoryThread> GetPassageThread(StoryPassage passage)
		{
			return passage.MainThread;
		}

		void Enter(string passageName)
		{
			//this.SaveData = this.Save();

			_timeAccumulated = 0f;
			_timeChangedToPlay = Time.time;

			this.InsertStack.Clear();
			this.Output.Clear();
			_styleGroupStack.Clear();
			_passageUpdateCues = null;

			StoryPassage passage = GetPassage(passageName);
			this.CurrentPassage = passage;

			// Prepare the thread enumerator
			_currentThread = CollapseThread(GetPassageThread(passage).Invoke()).GetEnumerator();
			CurrentLinkInAction = null;

			this.State = StoryState.Playing;

			_callbacks
				.Reset()
				.Add(() =>
				{
					// Invoke the general passage enter event
					if (this.OnPassageEnter != null)
						this.OnPassageEnter(passage);
				})
				.Add(() =>
				{
					SendMessage("OnStoryPassageEnter", this.CurrentPassage, SendMessageOptions.DontRequireReceiver);
				})
				.Add(CuesGet(passage.Name, CueType.Enter))
				.OnComplete(() => ExecuteCurrentThread())
				.Invoke();
		}

		/// <summary>
		/// Executes the current thread by advancing its enumerator, sending its output and invoking callbacks.
		/// </summary>
		void ExecuteCurrentThread()
		{
			Abort aborted = null;
			UpdateCuesRefresh();

			// Find some output that isn't null
			while (this.State == StoryState.Playing)
			{
				_callbacks.Reset();

				StoryOutput output = null;
				while (_currentThread.MoveNext())
				{
					if (_currentThread.Current != null)
					{
						output = _currentThread.Current;
						break;
					}
				}

				if (output == null)
					break;

				// Process the output
				if (output is Abort)
				{
					// Abort this thread
					aborted = (Abort)output;
					break;
				}
				else
				{
					OutputAdd(output);

					// Send output, invoke callbacks
					_callbacks
						.Add(() => OutputSend(output))
						.Add(CuesFind(CueType.Output), output);

					// Notify cues of embedded passages
					if (output is EmbedPassage)
					{
						_callbacks
							.Add(CuesGet(output.Name, CueType.Enter))
							.OnComplete(() => UpdateCuesRefresh());
					}

					_callbacks.Invoke();
				}
			}

			// Stop executing if story was paused
			if (this.State == StoryState.Paused)
				return;

			// End the current passage - is has ended or an "abort" output was encountered
			_currentThread.Dispose();
			_currentThread = null;

			this.State = StoryState.Idle;

			// Next passage, if any, will either be a goto or a link
			string goToPassage =
				aborted != null ? aborted.GoToPassage :
				CurrentLinkInAction != null ? CurrentLinkInAction.PassageName :
				null;

			_callbacks
				.Reset()
				.Add(() =>
				{
					// Invoke the general passage enter event
					if (this.OnPassageDone != null)
						this.OnPassageDone(this.CurrentPassage);
				})
				.Add(() =>
				{
					SendMessage("OnStoryPassageDone", this.CurrentPassage, SendMessageOptions.DontRequireReceiver);
				})
				.Add(() =>
				{
					// Invoke the done cue - either for main or for a link
					if (CurrentLinkInAction == null)
					{
						CuesInvoke(CuesFind(CueType.Done));
					}
					else
					{
						CuesInvoke(CuesFind(CueType.Done, CurrentLinkInAction.Name));
						CurrentLinkInAction = null;
						NumberOfLinksDone++;
					}
				})
				.OnComplete(() =>
				{
					// Now that the link is done, go to its passage
					if (goToPassage != null)
						GoTo(goToPassage);
				})
				.Invoke();	
		}

		/// <summary>
		/// Invokes and bubbles up output of embedded fragments and passages.
		/// </summary>
		IStoryThread CollapseThread(IStoryThread thread)
		{
			foreach (StoryOutput output in thread)
			{
				//foreach (TwineOutput scopeTag in ScopeOutputTags())
				//	yield return scopeTag;

				if (output is Embed)
				{
					var embed = (Embed) output;
					IStoryThread embeddedThread;
					if (embed is EmbedPassage)
					{
						var embedInfo = (EmbedPassage)embed;
						StoryPassage passage = GetPassage(embedInfo.Name);
						embeddedThread = passage.MainThread();
					}
					else if (embed is EmbedFragment)
					{
						var embedInfo = (EmbedFragment)embed;
                        embeddedThread = embedInfo.GetThread();
					}
					else
						continue;

					// First yield the embed
					yield return embed;

					// Output the content
					foreach (StoryOutput innerOutput in CollapseThread(embeddedThread))
					{
						if (innerOutput != null)
							innerOutput.EmbedInfo = embed;
						yield return innerOutput;
					}
				}
				else
					yield return output;
			}
		}

		void OutputAdd(StoryOutput output)
		{
			// Insert the output into the right place
			int insertIndex = InsertStack.Count > 0 ? InsertStack.Peek() : -1;

			if (insertIndex < 0)
			{
				output.Index = this.Output.Count;
				this.Output.Add(output);
			}
			else
			{
				// When a valid insert index is specified, update the following outputs' index
				output.Index = insertIndex;
				this.Output.Insert(insertIndex, output);
				OutputUpdateIndexes(insertIndex + 1);
			}

			// Increase the topmost index
			if (InsertStack.Count > 0 && insertIndex >= 0)
				InsertStack.Push(InsertStack.Pop() + 1);
		}

		void OutputSend(StoryOutput output, bool add = false)
		{
			if (_styleGroupStack.Count > 0)
				output.StyleGroup = _styleGroupStack.Peek();

			if (add)
				OutputAdd(output);

			if (OnOutput != null)
				OnOutput(output);
		}

		protected void OutputRemove(StoryOutput output)
		{
			if (this.Output.Remove(output))
			{
				_allowStateChanging = false;
				if (OnOutputRemoved != null)
					OnOutputRemoved(output);
				_allowStateChanging = true;
				OutputUpdateIndexes(output.Index);
			}
		}

		void OutputUpdateIndexes(int startIndex)
		{
			for (int i = startIndex; i < this.Output.Count; i++)
				this.Output[i].Index = i;
		}


		public IEnumerable<StoryLink> GetCurrentLinks()
		{
			return this.Output.Where(o => o is StoryLink).Cast<StoryLink>();
		}

		public IEnumerable<StoryText> GetCurrentText()
		{
			return this.Output.Where(o => o is StoryText).Cast<StoryText>();
		}

		public bool IsFirstVisitToPassage
		{
			get { return PassageHistory.Count(p => p == this.CurrentPassage.Name) == 0; }
		}

		// ---------------------------------
		// Grouping and style

		protected StyleScope styleScope(string key, object value)
		{
			return styleScope(new Style(key, value));
		}

		protected StyleScope styleScope(Style style)
		{
			var group = new StyleGroup(style);

			// Don't allow pausing story while sending a style group
			_allowStateChanging = false;

			OutputSend(group, add: true);

			_allowStateChanging = true;

			return styleScope(group);
		}

		protected StyleScope styleScope(StyleGroup styleGroup)
		{
			var scope = new StyleScope(styleGroup);
			scope.OnDisposed += StyleScopeClose;

			_styleGroupStack.Push(scope.Group);

			return scope;
		}

		void StyleScopeClose(StyleScope scope)
		{
			scope.OnDisposed -= StyleScopeClose;
			if (_styleGroupStack.Peek() != scope.Group)
				throw new System.Exception("Unexpected style group attempting to close.");

			_styleGroupStack.Pop();
		}

		public StyleGroup CurrentStyleGroup
		{
			get { return _styleGroupStack.Peek(); }
		}

		public Style GetCurrentStyle()
		{
			StyleGroup group = _styleGroupStack.Count > 0 ? _styleGroupStack.Peek() : null;
			if (group != null)
				return group.GetAppliedStyle() + group.Style; // Combine styles
			else
				return new Style();
		}
		
		// ---------------------------------
		// Links

		public void DoLink(StoryLink link)
		{
			if (this.State != StoryState.Idle)
			{
				throw new InvalidOperationException(
					// Paused
					this.State == StoryState.Paused ?
						"The story is currently paused. Resume() must be called before a link can be used." :
					// Playing
					this.State == StoryState.Playing || this.State == StoryState.Exiting ?
						"A link can be used only when the story is in the Idle state." :
					// Complete
						"The story is complete. Reset() must be called before it can be played again."
					);
			}

			CurrentLinkInAction = link;
			
			// Action might invoke a fragment method, in which case we need to process it with cues etc.
			IStoryThread linkActionThread = (link.Action ?? EmptyLinkAction).Invoke();
			
			// Prepare the fragment thread enumerator
			_currentThread = CollapseThread(linkActionThread).GetEnumerator();

			// Resume story, this time with the actoin thread
			this.State = StoryState.Playing;

			if (this.OnLinkEnter != null)
				this.OnLinkEnter(CurrentPassage, link);

			// Invoke a link enter cue
			CuesInvoke(CuesFind(CueType.Enter, CurrentLinkInAction.Name));

			if (this.State == StoryState.Playing)
				ExecuteCurrentThread();
		}

		public void DoLink(int linkIndex)
		{
			DoLink(this.GetCurrentLinks().ElementAt(linkIndex));
		}

		public void DoLink(string linkName)
		{
			StoryLink link = GetLink(linkName, true);
			DoLink(link);
		}

		public bool HasLink(string linkName)
		{
			return GetLink(linkName) != null;
		}

		public StoryLink GetLink(string linkName, bool throwException = false)
		{
			StoryLink link = this.GetCurrentLinks()
				.Where(lnk => string.Equals(lnk.Name, linkName, System.StringComparison.OrdinalIgnoreCase))
				.FirstOrDefault();

			if (link == null && throwException)
				throw new StoryException(string.Format("There is no available link with the name '{0}' in the passage '{1}'.", linkName, this.CurrentPassage.Name));

			return link;
		}

		[Obsolete("Use DoLink instead.")]
		public void Advance(StoryLink link)
		{
			DoLink(link);
		}

		[Obsolete("Use DoLink instead.")]
		public void Advance(int linkIndex)
		{
			DoLink(linkIndex);
		}

		[Obsolete("Use DoLink instead.")]
		public void Advance(string linkName)
		{
			DoLink(linkName);
		}

		IStoryThread EmptyLinkAction()
		{
			yield break;
		}

		// ---------------------------------
		// Cues

		static Regex _validPassageNameRegex = new Regex("^[a-z_][a-z0-9_]*$", RegexOptions.IgnoreCase);
		const BindingFlags _cueMethodFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase;

		void Update()
		{
			CuesInvoke(_passageUpdateCues);
		}

		void UpdateCuesRefresh()
		{
			// Get update cues for calling during update
			_passageUpdateCues = CuesFind(CueType.Update, reverse: false, allowCoroutines: false).ToArray();
		}

		void CuesInvoke(IEnumerable<Cue> cues, params object[] args)
		{
			if (cues == null)
				return;

			if (cues is Cue[])
			{
				var ar = (Cue[]) cues;
				for (int i = 0; i < ar.Length; i++)
					CueInvoke(ar[i], args);
			}
			else
			{
				foreach (Cue cue in cues)
					CueInvoke(cue, args);
			}
		}


		IEnumerable<Cue> CuesFind(CueType cueType, string linkName = null, bool reverse = false, bool allowCoroutines = true)
		{
			// Get main passage's cues
			List<Cue> mainCues = CuesGet(this.CurrentPassage.Name, cueType, linkName, allowCoroutines);

			// Return them here only if not reversing
			if (!reverse && mainCues != null)
			{
				for (int h = 0; h < mainCues.Count; h++)
					yield return mainCues[h];
			}

			// Note: Since 2.0, the Output list can be rearranged by changing the InsertStack.
			// Consequently, later embedded passages' cues can be triggered before earlier embedded
			// passages' cues if they were inserted higher up in the list.
			int c = 0;
			for(
				int i = reverse ? this.Output.Count - 1 : 0;
				reverse ? i >= 0 : i < this.Output.Count;
				c++, i = i + (reverse ? -1 : 1)
				)
			{
				EmbedPassage passageEmbed = this.Output[i] as EmbedPassage;
				if (passageEmbed == null)
					continue;

				List<Cue> cues = CuesGet(passageEmbed.Name, cueType, linkName, allowCoroutines);
				if (cues != null)
				{
					for (int h = 0; h < cues.Count; h++)
						yield return cues[h];
				}
			}

			// Reversing, so return the main cues now
			if (reverse && mainCues != null)
			{
				for (int h = 0; h < mainCues.Count; h++)
					yield return mainCues[h];
			}
		}

		internal void CueInvoke(Cue cue, object[] args)
		{
			object result = null;
			try { result = cue.method.Invoke(cue.target, args); }
			catch(TargetParameterCountException)
			{
				Debug.LogWarningFormat("The cue '{0}' doesn't have the right parameters so it is being ignored.",
					cue.method.Name
				);
				return;
			}

			if (result is IEnumerator)
				StartCoroutine(((IEnumerator)result));
		}

		MonoBehaviour[] CueGetTargets()
		{
			if (_cueTargets == null)
			{
				// ...................
				// Get all hook targets
				GameObject[] cueTargets;
				if (this.AdditionalCues != null)
				{
					cueTargets = new GameObject[this.AdditionalCues.Count + 1];
					cueTargets[0] = this.gameObject;
					this.AdditionalCues.CopyTo(cueTargets, 1);
				}
				else
					cueTargets = new GameObject[] { this.gameObject };

				// Get all types
				var sources = new List<MonoBehaviour>();
				for (int i = 0; i < cueTargets.Length; i++)
					sources.AddRange(cueTargets[i].GetComponents<MonoBehaviour>());

				_cueTargets = sources.ToArray();
			}

			return _cueTargets;
		}

		List<Cue> CuesGet(string passageName, CueType cueType, string linkName = null, bool allowCoroutines = true)
		{
			string cueName = cueType.ToString();

			string methodName = passageName + "_" + (linkName != null ? linkName + "_" : null) + cueName;

			List<Cue> cues = null;

			if (!_cueCache.TryGetValue(methodName, out cues))
			{
				MonoBehaviour[] targets = CueGetTargets();
				var methodsFound = new List<MethodInfo>();

				for (int i = 0; i < targets.Length; i++)
				{
					Type targetType = targets[i].GetType();
					methodsFound.Clear();

					// Get methods with attribute and sort by order
					methodsFound.AddRange(targetType.GetMethods(_cueMethodFlags)
						.Where(m => m.GetCustomAttributes(typeof(StoryCueAttribute), true)
							.Cast<StoryCueAttribute>()
							.Where(attr => attr.PassageName == passageName && attr.LinkName == linkName && attr.Cue == cueType)
							.Count() > 0
						)
						.OrderBy(m => m.GetCustomAttributes(typeof(StoryCueAttribute), true)
							.Cast<StoryCueAttribute>()
							.FirstOrDefault()
							.Order
						)
					);

					// Get the method by name (if valid)
					if (_validPassageNameRegex.IsMatch(passageName))
					{
						MethodInfo methodByName = targetType.GetMethod(methodName, _cueMethodFlags);

						// Only add it if doesn't have a StoryCue attribute
						if (methodByName != null && methodByName.GetCustomAttributes(typeof(StoryCueAttribute), true).Length == 0)
							methodsFound.Add(methodByName);
					}

					// Now ensure that all methods are valid and add them as cues
					foreach (MethodInfo method in methodsFound)
					{
						// Validate the found method
						if (allowCoroutines)
						{
							if (method.ReturnType != typeof(void) && !typeof(IEnumerator).IsAssignableFrom(method.ReturnType))
							{
								Debug.LogWarning(targetType.Name + "." + methodName + " must return void or IEnumerator in order to be used as a cue.");
								continue;
							}
						}
						else
						{
							if (method.ReturnType != typeof(void))
							{
								Debug.LogWarning(targetType.Name + "." + methodName + " must return void in order to be used as a cue.");
								continue;
							}
						}

						// Init the method list
						if (cues == null)
							cues = new List<Cue>();

						cues.Add(new Cue() { method = method, target = targets[i] });
					}
				}

				// Cache the method list even if it's null so we don't do another lookup next time around (lazy load)
				_cueCache.Add(methodName, cues);
			}

			return cues;
		}

		public void CuesClear()
		{
			_cueCache.Clear();
			_cueTargets = null;
		}


		// ---------------------------------
		// Shorthand functions

		protected StoryVar v(string val)
		{
			return new StoryVar(val);
		}

		protected StoryVar v(double val)
		{
			return new StoryVar(val);
		}

		protected StoryVar v(int val)
		{
			return new StoryVar(val);
		}

		protected StoryVar v(bool val)
		{
			return new StoryVar(val);
		}

		protected StoryVar v(object val)
		{
			return new StoryVar(val);
		}

		protected StoryText text(StoryVar text)
		{
			return new StoryText(StoryVar.ConvertTo<string>(text.Value, strict: false));
		}

		protected HtmlTag htmlTag(StoryVar text)
		{
			return new HtmlTag(StoryVar.ConvertTo<string>(text.Value, strict: false));
		}

		protected LineBreak lineBreak()
		{
			return new LineBreak();
		}

		protected StoryLink link(string text, string passageName, Func<IStoryThread> action)
		{
			return new StoryLink(text, passageName, action);
		}

		protected Abort abort(string goToPassage)
		{
			return new Abort(goToPassage);
		}

		protected EmbedFragment fragment(Func<IStoryThread> action)
		{
			return new EmbedFragment(action);
		}

		protected EmbedPassage passage(string passageName, params StoryVar[] parameters)
		{
			return new EmbedPassage(passageName, parameters);
		}

		protected Style style(string key, object value)
		{
			return new Style(key, value);
		}

		protected Style style(StoryVar expression)
		{
			return new Style(expression);
		}
	}

	//[Serializable]
	//public class StorySaveData
	//{
	//	public string PassageToResume;
	//	public List<string> PassageHistory;
	//	public Dictionary<string, StoryVar> Variables;
	//}

	class CallbackList
	{
		Story _story;
		List<Action> _actions = new List<Action>();
		int _current;
		Action _onComplete;

		public CallbackList(Story story)
		{
			_story = story;
			Reset();
		}

		public CallbackList Reset()
		{
			_actions.Clear();
			_current = -1;
			_onComplete = null;
			return this;
		}

		public CallbackList Add(Action action)
		{
			if (Started)
				throw new InvalidOperationException("Callback list has already been started");
			_actions.Add(action);
			return this;
		}

		public CallbackList Add(IEnumerable<Story.Cue> cues, params object[] args)
		{
			if (cues == null)
				return this;

			if (cues is Story.Cue[])
			{
				var ar = (Story.Cue[])cues;
				for (int i = 0; i < ar.Length; i++)
					_actions.Add(CueInvoker(ar[i], args));
			}
			else
			{
				foreach (Story.Cue cue in cues)
					_actions.Add(CueInvoker(cue, args));
			}
			return this;
		}

		public CallbackList OnComplete(Action onComplete)
		{
			if (Started)
				throw new InvalidOperationException("Callback list has already been started");
			_onComplete = onComplete;
			return this;
		}

		Action CueInvoker(Story.Cue cue, object[] args)
		{
			return () => _story.CueInvoke(cue, args);
		}

		public bool Started
		{
			get { return _current >= 0; }
		}

		public bool Completed
		{
			get { return _current >= _actions.Count; }
		}

		public CallbackList Invoke()
		{
			if (Completed)
				throw new InvalidOperationException("Callback list is complete");

			while (!Completed)
			{			
				if (_current < _actions.Count)
					_current++;

				if (Completed)
					break;

				_actions[_current].Invoke();

				if (_story.State == StoryState.Paused)
					return this;
			}

			if (_onComplete != null)
				_onComplete.Invoke();

			return this;
		}
	}
}