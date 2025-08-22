using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Ionic.Zip;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Networking;
using Vosk;

public enum SupportedLanguage
{
	Auto,
	English,
	Chinese
}

public class VoskSpeechToText : MonoBehaviour
{
	[Tooltip("Location of the Chinese model, relative to the Streaming Assets folder.")]
	public string CnModelPath = "vosk-model-small-cn-0.22.zip";
	
	[Tooltip("Location of the English model, relative to the Streaming Assets folder.")]
	public string EnModelPath = "vosk-model-small-en-us-0.15.zip";

	[Tooltip("Language detection mode. Auto will detect language automatically.")]
	public SupportedLanguage LanguageMode = SupportedLanguage.Auto;

	[Tooltip("The source of the microphone input.")]

	public VoiceProcessor VoiceProcessor;
	[Tooltip("The Max number of alternatives that will be processed.")]
	public int MaxAlternatives = 3;

	[Tooltip("How long should we record before restarting?")]
	public float MaxRecordLength = 5;

	[Tooltip("Should the recognizer start when the application is launched?")]
	public bool AutoStart = true;

	[Tooltip("The phrases that will be detected. If left empty, all words will be detected.")]
	public List<string> KeyPhrases = new List<string>();

	//Cached version of the Vosk Models.
	private Model _chineseModel;
	private Model _englishModel;

	//Cached version of the Vosk recognizers.
	private VoskRecognizer _chineseRecognizer;
	private VoskRecognizer _englishRecognizer;
	private VoskRecognizer _currentRecognizer;

	//Language detection variables
	private SupportedLanguage _detectedLanguage = SupportedLanguage.Auto;
	private int _chineseConfidenceCount = 0;
	private int _englishConfidenceCount = 0;
	private const int LANGUAGE_DETECTION_THRESHOLD = 3; // Number of consistent detections needed

	//Conditional flag to see if recognizers have already been created.
	private bool _recognizersReady;

	//Holds all of the audio data until the user stops talking.
	private readonly List<short> _buffer = new List<short>();

	//Called when the the state of the controller changes.
	public Action<string> OnStatusUpdated;

	//Called when language is detected or changed
	public Action<SupportedLanguage> OnLanguageDetected;

	//Called after the user is done speaking and vosk processes the audio.
	public Action<string> OnTranscriptionResult;

	//The absolute path to the decompressed model folders.
	private string _chineseDecompressedModelPath;
	private string _englishDecompressedModelPath;

	//A string that contains the keywords in Json Array format
	private string _grammar = "";

	//Flag that is used to wait for the model file to decompress successfully.
	private bool _isDecompressing;

	//Flag that is used to wait for the the script to start successfully.
	private bool _isInitializing;

	//Flag that is used to check if Vosk was started.
	private bool _didInit;

	//Threading Logic

	// Flag to signal we are ending
	private bool _running;
	private System.Threading.Thread _speechProcessingThread; // Keep reference to the thread

	//Thread safe queue of microphone data.
	private readonly ConcurrentQueue<short[]> _threadedBufferQueue = new ConcurrentQueue<short[]>();

	//Thread safe queue of results from both models
	private readonly ConcurrentQueue<ModelResult> _threadedResultQueue = new ConcurrentQueue<ModelResult>();

	// Structure to hold results from both models with confidence scores
	public struct ModelResult
	{
		public string chineseResult;
		public string englishResult;
		public float chineseConfidence;
		public float englishConfidence;
		public SupportedLanguage detectedLanguage;
		public string bestResult;
	}

	static readonly ProfilerMarker voskRecognizerCreateMarker = new ProfilerMarker("VoskRecognizer.Create");
	static readonly ProfilerMarker voskRecognizerReadMarker = new ProfilerMarker("VoskRecognizer.AcceptWaveform");

	//Auto-initialize Vosk when the component starts
	void Start()
	{
		if (AutoStart)
		{
			StartVoskStt();
		}
	}

	//If Auto start is enabled, starts vosk speech to text.

	/// <summary>
	/// Start Vosk Speech to text
	/// </summary>
	/// <param name="keyPhrases">A list of keywords/phrases. Keywords need to exist in the models dictionary, so some words like "webview" are better detected as two more common words "web view".</param>
	/// <param name="modelPath">The path to the model folder relative to StreamingAssets. If the path has a .zip ending, it will be decompressed into the application data persistent folder.</param>
	/// <param name="startMicrophone">"Should the microphone after vosk initializes?</param>
	/// <param name="maxAlternatives">The maximum number of alternative phrases detected</param>
	public void StartVoskStt(List<string> keyPhrases = null, string modelPath = default, bool startMicrophone = false, int maxAlternatives = 3)
	{
		if (_isInitializing || _didInit) return;

		// Note: modelPath parameter is kept for backward compatibility but not used in dual-model setup

		if (keyPhrases != null)
		{
			KeyPhrases = keyPhrases;
		}

		MaxAlternatives = maxAlternatives;
		StartCoroutine(DoStartVoskStt(startMicrophone));
	}

	//Decompress model, load settings, start Vosk and optionally start the microphone
	private IEnumerator DoStartVoskStt(bool startMicrophone)
	{
		_isInitializing = true;
		yield return WaitForMicrophoneInput();

		// Decompress both models
		yield return DecompressBothModels();

		OnStatusUpdated?.Invoke("Loading Chinese Model from: " + _chineseDecompressedModelPath);
		_chineseModel = new Model(_chineseDecompressedModelPath);
		
		yield return null;
		
		OnStatusUpdated?.Invoke("Loading English Model from: " + _englishDecompressedModelPath);
		_englishModel = new Model(_englishDecompressedModelPath);

		yield return null;

		VoiceProcessor.OnFrameCaptured += VoiceProcessorOnFrameCaptured;

		if (startMicrophone)
			VoiceProcessor.StartRecording();

		_isInitializing = false;
		_didInit = true;
	}

	//Translates the KeyPhraseses into a json array and appends the `[unk]` keyword at the end to tell vosk to filter other phrases.
	private void UpdateGrammar()
	{
		if (KeyPhrases.Count == 0)
		{
			_grammar = "";
			return;
		}

		JSONArray keywords = new JSONArray();
		foreach (string keyphrase in KeyPhrases)
		{
			keywords.Add(new JSONString(keyphrase.ToLower()));
		}

		keywords.Add(new JSONString("[unk]"));

		_grammar = keywords.ToString();
	}

	//Decompress the model zip file or return the location of the decompressed files. [DEPRECATED - Use DecompressBothModels instead]
	private IEnumerator Decompress()
	{
		// This method is kept for backward compatibility but is no longer used
		// The new dual-model system uses DecompressBothModels() instead
		yield break;
	}

	///The function that is called when the zip file extraction process is updated.
	private void ZipFileOnExtractProgress(object sender, ExtractProgressEventArgs e)
	{
		if (e.EventType == ZipProgressEventType.Extracting_AfterExtractAll)
		{
			_isDecompressing = true;
			_chineseDecompressedModelPath = e.ExtractLocation;
		}
	}

	//Wait until microphones are initialized
	private IEnumerator WaitForMicrophoneInput()
	{
		while (Microphone.devices.Length <= 0)
			yield return null;
	}

	//Starts only the speech recognition processing thread without controlling VoiceProcessor
	public void StartSpeechProcessing()
	{
		if (!_running && _speechProcessingThread == null)
		{
			_running = true;
			// Create and store reference to the thread for proper cleanup
			_speechProcessingThread = new System.Threading.Thread(ThreadedWorkSafe);
			_speechProcessingThread.IsBackground = true;
			_speechProcessingThread.Name = "VoskSpeechProcessing";
			_speechProcessingThread.Start();
		}
	}

	//Stops only the speech recognition processing thread
	public void StopSpeechProcessing()
	{
		_running = false;
		
		// Wait for thread to finish gracefully
		if (_speechProcessingThread != null && _speechProcessingThread.IsAlive)
		{
			try
			{
				// Give the thread a moment to finish naturally
				if (!_speechProcessingThread.Join(1000)) // Wait up to 1 second
				{
					Debug.LogWarning("Speech processing thread did not terminate gracefully");
				}
			}
			catch (System.Exception ex)
			{
				Debug.LogWarning($"Exception while stopping speech thread: {ex.Message}");
			}
			finally
			{
				_speechProcessingThread = null;
			}
		}
	}

	//Unity-safe threaded work method - now processes both models simultaneously
	private void ThreadedWorkSafe()
	{
		try
		{
			voskRecognizerCreateMarker.Begin();
			if (!_recognizersReady)
			{
				UpdateGrammar();

				//Create both recognizers
				if (string.IsNullOrEmpty(_grammar))
				{
					_chineseRecognizer = new VoskRecognizer(_chineseModel, 16000.0f);
					_englishRecognizer = new VoskRecognizer(_englishModel, 16000.0f);
				}
				else
				{
					_chineseRecognizer = new VoskRecognizer(_chineseModel, 16000.0f, _grammar);
					_englishRecognizer = new VoskRecognizer(_englishModel, 16000.0f, _grammar);
				}

				_chineseRecognizer.SetMaxAlternatives(MaxAlternatives);
				_englishRecognizer.SetMaxAlternatives(MaxAlternatives);
				_recognizersReady = true;
			}

			voskRecognizerCreateMarker.End();
			voskRecognizerReadMarker.Begin();

			while (_running)
			{
				if (_threadedBufferQueue.TryDequeue(out short[] voiceResult))
				{
					// Process the same audio through BOTH models simultaneously
					bool chineseHasResult = _chineseRecognizer.AcceptWaveform(voiceResult, voiceResult.Length);
					bool englishHasResult = _englishRecognizer.AcceptWaveform(voiceResult, voiceResult.Length);

					// If either model has a result, compare both
					if (chineseHasResult || englishHasResult)
					{
						string chineseResult = chineseHasResult ? _chineseRecognizer.Result() : "";
						string englishResult = englishHasResult ? _englishRecognizer.Result() : "";

						// Calculate confidence scores and determine best result
						ModelResult dualResult = CompareModelResults(chineseResult, englishResult);
						
						_threadedResultQueue.Enqueue(dualResult);

                        // Re-create recognizers for the next utterance to avoid accumulating results
                        _chineseRecognizer.Dispose();
                        _englishRecognizer.Dispose();

                        if (string.IsNullOrEmpty(_grammar))
                        {
                            _chineseRecognizer = new VoskRecognizer(_chineseModel, 16000.0f);
                            _englishRecognizer = new VoskRecognizer(_englishModel, 16000.0f);
                        }
                        else
                        {
                            _chineseRecognizer = new VoskRecognizer(_chineseModel, 16000.0f, _grammar);
                            _englishRecognizer = new VoskRecognizer(_englishModel, 16000.0f, _grammar);
                        }

                        _chineseRecognizer.SetMaxAlternatives(MaxAlternatives);
                        _englishRecognizer.SetMaxAlternatives(MaxAlternatives);
					}
				}
				else
				{
					// Wait for some data
					System.Threading.Thread.Sleep(100);
				}
			}

			voskRecognizerReadMarker.End();
		}
		catch (System.Exception ex)
		{
			Debug.LogError($"Error in Vosk dual threading: {ex.Message}");
		}
	}

	/// <summary>
	/// Compare results from both models and determine which one has higher confidence
	/// </summary>
	private ModelResult CompareModelResults(string chineseResult, string englishResult)
	{
		var result = new ModelResult
		{
			chineseResult = chineseResult,
			englishResult = englishResult,
			chineseConfidence = CalculateConfidenceForModel(chineseResult, true), // true = Chinese model
			englishConfidence = CalculateConfidenceForModel(englishResult, false) // false = English model
		};

		// Determine which result has higher confidence
		if (result.chineseConfidence > result.englishConfidence)
		{
			result.detectedLanguage = SupportedLanguage.Chinese;
			result.bestResult = chineseResult;
		}
		else if (result.englishConfidence > result.chineseConfidence)
		{
			result.detectedLanguage = SupportedLanguage.English;
			result.bestResult = englishResult;
		}
		else
		{
			// If confidence is equal, use character-based detection as fallback
			var chineseTextLang = DetectLanguageFromText(chineseResult);
			var englishTextLang = DetectLanguageFromText(englishResult);
			
			if (chineseTextLang == SupportedLanguage.Chinese)
			{
				result.detectedLanguage = SupportedLanguage.Chinese;
				result.bestResult = chineseResult;
			}
			else
			{
				result.detectedLanguage = SupportedLanguage.English;
				result.bestResult = englishResult;
			}
		}

		Debug.Log($"Model Comparison - Chinese: {result.chineseConfidence:F3}, English: {result.englishConfidence:F3}, Winner: {result.detectedLanguage}");
		
		return result;
	}

	/// <summary>
	/// Calculate confidence score for a specific model with language-appropriate adjustments
	/// </summary>
	private float CalculateConfidenceForModel(string voskResult, bool isChineseModel)
	{
		if (string.IsNullOrEmpty(voskResult))
			return 0f;

		try
		{
			var jsonData = JSON.Parse(voskResult);
			float baseConfidence = 0f;
			string text = "";
			
			// Check if we have alternatives with confidence scores
			if (jsonData["alternatives"] != null && jsonData["alternatives"].IsArray)
			{
				var alternatives = jsonData["alternatives"].AsArray;
				if (alternatives.Count > 0)
				{
					var bestAlternative = alternatives[0];
					if (bestAlternative["confidence"] != null)
					{
						baseConfidence = bestAlternative["confidence"].AsFloat;
					}
					if (bestAlternative["text"] != null)
					{
						text = bestAlternative["text"].Value;
					}
				}
			}
			
			// If no confidence in alternatives, look for top-level confidence and text
			if (baseConfidence == 0f && jsonData["confidence"] != null)
			{
				baseConfidence = jsonData["confidence"].AsFloat;
			}
			if (string.IsNullOrEmpty(text) && jsonData["text"] != null)
			{
				text = jsonData["text"].Value;
			}
			
			// If we still don't have text, return 0
			if (string.IsNullOrWhiteSpace(text))
				return 0f;
			
			// Vosk confidence scores are often very high numbers (200-400+)
			// Normalize them to a 0-1 range for our calculations
			float normalizedConfidence = baseConfidence / 1000f; // Normalize high Vosk scores
			if (normalizedConfidence > 1f) normalizedConfidence = 1f;
			
			// If we still don't have a base confidence, calculate based on text quality
			if (normalizedConfidence == 0f)
			{
				// Basic heuristic: longer, non-empty text typically means higher confidence
				float lengthScore = Mathf.Min(text.Length / 20f, 1f); // Normalize to 0-1
				normalizedConfidence = lengthScore * 0.5f; // Conservative fallback
			}
			
			Debug.Log($"{(isChineseModel ? "Chinese" : "English")} model - Raw confidence: {baseConfidence}, Normalized: {normalizedConfidence:F3}, Text: '{text}'");
			
			// Apply language-specific adjustments based on actual model type
			return ApplyModelSpecificConfidenceAdjustment(normalizedConfidence, text, isChineseModel);
		}
		catch (System.Exception ex)
		{
			Debug.LogWarning($"Error calculating confidence for {(isChineseModel ? "Chinese" : "English")} model: {ex.Message}");
			return 0f;
		}
	}

	/// <summary>
	/// Apply model-specific confidence adjustments based on text content and model type
	/// </summary>
	private float ApplyModelSpecificConfidenceAdjustment(float baseConfidence, string text, bool isChineseModel)
	{
		// Analyze text content for language characteristics
		int chineseCharCount = 0;
		int englishCharCount = 0;
		int totalChars = 0;

		foreach (char c in text)
		{
			if (char.IsLetter(c) || IsChinese(c))
			{
				totalChars++;
				if (IsChinese(c))
				{
					chineseCharCount++;
				}
				else if (char.IsLetter(c) && ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')))
				{
					englishCharCount++;
				}
			}
		}

		if (totalChars == 0)
			return baseConfidence;

		float chineseRatio = (float)chineseCharCount / totalChars;
		float englishRatio = (float)englishCharCount / totalChars;
		
		// Language-specific confidence adjustments
		float adjustedConfidence = baseConfidence;
		
		// If text contains mostly Chinese characters
		if (chineseRatio > 0.3f) // Lower threshold for Chinese detection
		{
			if (isChineseModel)
			{
				// Boost Chinese model for Chinese text
				adjustedConfidence *= (1.0f + chineseRatio); // 1.3-2.0x multiplier
			}
			else
			{
				// Heavily penalize English model for Chinese text
				adjustedConfidence *= (0.1f + (1.0f - chineseRatio) * 0.2f); // 0.1-0.24x multiplier
			}
		}
		// If text contains mostly English characters
		else if (englishRatio > 0.8f) // High threshold for English detection
		{
			if (!isChineseModel)
			{
				// Boost English model for English text
				adjustedConfidence *= (1.0f + englishRatio * 0.5f); // 1.4-1.5x multiplier
			}
			else
			{
				// Heavily penalize Chinese model for English text
				adjustedConfidence *= (0.1f + (1.0f - englishRatio) * 0.3f); // 0.1-0.16x multiplier
			}
		}
		// Mixed content (some Chinese, some English)
		else if (chineseRatio > 0.1f && englishRatio > 0.1f)
		{
			// For mixed content, slightly favor the appropriate model
			if (isChineseModel && chineseRatio > englishRatio)
			{
				adjustedConfidence *= 1.1f;
			}
			else if (!isChineseModel && englishRatio > chineseRatio)
			{
				adjustedConfidence *= 1.1f;
			}
			else
			{
				adjustedConfidence *= 0.9f; // Slight penalty for wrong model
			}
		}
		// Mostly non-letter content (numbers, punctuation, etc.)
		else
		{
			// Apply mild penalty if model doesn't match expected content
			adjustedConfidence *= 0.9f;
		}
		
		// Ensure confidence stays within bounds
		return Mathf.Clamp01(adjustedConfidence);
	}
	
	//Calls the On Phrase Recognized event on the Unity Thread
	void Update()
	{
		if (_threadedResultQueue.TryDequeue(out ModelResult voiceResult))
		{
			// Process language detection if in Auto mode
			if (LanguageMode == SupportedLanguage.Auto)
			{
				ProcessLanguageDetection(voiceResult);
			}
			
			// Invoke both events - the original for backward compatibility and the detailed one
			OnTranscriptionResult?.Invoke(voiceResult.bestResult);
			OnDetailedTranscriptionResult?.Invoke(voiceResult);
		}
	}

	/// <summary>
	/// Detects language based on character patterns in transcription results
	/// </summary>
	/// <param name="text">The transcribed text to analyze</param>
	/// <returns>Detected language</returns>
	private SupportedLanguage DetectLanguageFromText(string text)
	{
		if (string.IsNullOrEmpty(text))
			return SupportedLanguage.Auto;

		// Parse JSON to extract the actual text
		try
		{
			var jsonData = JSON.Parse(text);
			string actualText = jsonData["text"];
			
			if (string.IsNullOrEmpty(actualText))
				return SupportedLanguage.Auto;

			int chineseCharCount = 0;
			int totalChars = 0;

			foreach (char c in actualText)
			{
				if (char.IsLetter(c) || char.IsDigit(c))
				{
					totalChars++;
					// Check if character is in Chinese Unicode ranges
					if (IsChinese(c))
					{
						chineseCharCount++;
					}
				}
			}

			if (totalChars == 0)
				return SupportedLanguage.Auto;

			// If more than 30% of characters are Chinese, consider it Chinese
			float chineseRatio = (float)chineseCharCount / totalChars;
			return chineseRatio > 0.3f ? SupportedLanguage.Chinese : SupportedLanguage.English;
		}
		catch (System.Exception)
		{
			// If JSON parsing fails, fall back to direct text analysis
			return AnalyzeRawText(text);
		}
	}

	/// <summary>
	/// Analyzes raw text for language detection
	/// </summary>
	private SupportedLanguage AnalyzeRawText(string text)
	{
		int chineseCharCount = 0;
		int totalChars = 0;

		foreach (char c in text)
		{
			if (char.IsLetter(c))
			{
				totalChars++;
				if (IsChinese(c))
				{
					chineseCharCount++;
				}
			}
		}

		if (totalChars == 0)
			return SupportedLanguage.Auto;

		float chineseRatio = (float)chineseCharCount / totalChars;
		return chineseRatio > 0.3f ? SupportedLanguage.Chinese : SupportedLanguage.English;
	}

	/// <summary>
	/// Checks if a character is Chinese (CJK Unified Ideographs)
	/// </summary>
	/// <param name="c">Character to check</param>
	/// <returns>True if character is Chinese</returns>
	private bool IsChinese(char c)
	{
		// Chinese character ranges (excluding ranges outside char type limit)
		return (c >= 0x4E00 && c <= 0x9FFF) ||     // CJK Unified Ideographs
			   (c >= 0x3400 && c <= 0x4DBF) ||     // CJK Extension A
			   (c >= 0xF900 && c <= 0xFAFF);       // CJK Compatibility Ideographs
		// Note: CJK Extension B (0x20000-0x2A6DF) is outside char range and excluded
	}

	/// <summary>
	/// Processes language detection and switches recognizers if needed
	/// </summary>
	/// <param name="transcriptionResult">The transcription result to analyze</param>
	private void ProcessLanguageDetection(ModelResult transcriptionResult)
	{
		var detectedLang = DetectLanguageFromText(transcriptionResult.bestResult);
		
		if (detectedLang != SupportedLanguage.Auto)
		{
			if (detectedLang == SupportedLanguage.Chinese)
			{
				_chineseConfidenceCount++;
				_englishConfidenceCount = 0;
			}
			else if (detectedLang == SupportedLanguage.English)
			{
				_englishConfidenceCount++;
				_chineseConfidenceCount = 0;
			}

			// Switch recognizer if confidence threshold is reached
			if (_chineseConfidenceCount >= LANGUAGE_DETECTION_THRESHOLD && _detectedLanguage != SupportedLanguage.Chinese)
			{
				SwitchToLanguage(SupportedLanguage.Chinese);
			}
			else if (_englishConfidenceCount >= LANGUAGE_DETECTION_THRESHOLD && _detectedLanguage != SupportedLanguage.English)
			{
				SwitchToLanguage(SupportedLanguage.English);
			}
		}
	}

	/// <summary>
	/// Switches to a specific language recognizer
	/// </summary>
	/// <param name="language">Language to switch to</param>
	private void SwitchToLanguage(SupportedLanguage language)
	{
		if (_detectedLanguage != language)
		{
			_detectedLanguage = language;
			
			switch (language)
			{
				case SupportedLanguage.Chinese:
					_currentRecognizer = _chineseRecognizer;
					Debug.Log("Switched to Chinese recognizer");
					break;
				case SupportedLanguage.English:
					_currentRecognizer = _englishRecognizer;
					Debug.Log("Switched to English recognizer");
					break;
			}
			
			OnLanguageDetected?.Invoke(language);
		}
	}

	/// <summary>
	/// Manually set the language mode (useful for testing or forced language selection)
	/// </summary>
	/// <param name="language">Language to set</param>
	public void SetLanguageMode(SupportedLanguage language)
	{
		LanguageMode = language;
		
		if (language != SupportedLanguage.Auto)
		{
			SwitchToLanguage(language);
		}
		else
		{
			// Reset confidence counters for auto detection
			_chineseConfidenceCount = 0;
			_englishConfidenceCount = 0;
			_detectedLanguage = SupportedLanguage.Auto;
		}
	}

	/// <summary>
	/// Get the currently detected language
	/// </summary>
	/// <returns>Currently detected language</returns>
	public SupportedLanguage GetDetectedLanguage()
	{
		return _detectedLanguage;
	}

	/// <summary>
	/// Get the latest model results from both Chinese and English recognizers
	/// </summary>
	/// <returns>ModelResult containing both results and confidence scores</returns>
	public ModelResult GetLatestModelResults()
	{
		if (_threadedResultQueue.TryPeek(out ModelResult latestResult))
		{
			return latestResult;
		}
		
		// Return empty result if no results available
		return new ModelResult
		{
			chineseResult = "",
			englishResult = "",
			chineseConfidence = 0f,
			englishConfidence = 0f,
			detectedLanguage = SupportedLanguage.Auto,
			bestResult = ""
		};
	}

	/// <summary>
	/// Event for detailed model results (includes both Chinese and English results)
	/// </summary>
	public Action<ModelResult> OnDetailedTranscriptionResult;

	/// <summary>
	/// Clean up threads and resources when component is destroyed
	/// </summary>
	void OnDestroy()
	{
		// Stop speech processing gracefully
		StopSpeechProcessing();
		
		// Clean up any remaining resources
		_chineseRecognizer?.Dispose();
		_englishRecognizer?.Dispose();
		_chineseModel?.Dispose();
		_englishModel?.Dispose();
	}

	/// <summary>
	/// Clean up threads when application quits
	/// </summary>
	void OnApplicationQuit()
	{
		StopSpeechProcessing();
	}

	// Decompress both model zip files or return the location of the decompressed files
	private IEnumerator DecompressBothModels()
	{
		// Decompress Chinese model
		yield return DecompressModel(CnModelPath, (path) => _chineseDecompressedModelPath = path, "Chinese");
		
		// Decompress English model
		yield return DecompressModel(EnModelPath, (path) => _englishDecompressedModelPath = path, "English");
	}

	// Decompress a specific model zip file or return the location of the decompressed files
	private IEnumerator DecompressModel(string modelPath, System.Action<string> setPath, string modelName)
	{
		string modelFileName = Path.GetFileNameWithoutExtension(modelPath);
		string decompressedPath = Path.Combine(Application.persistentDataPath, modelFileName);
		
		if (!Path.HasExtension(modelPath) || Directory.Exists(decompressedPath))
		{
			setPath(decompressedPath);
			yield break;
		}

		string dataPath = Path.Combine(Application.streamingAssetsPath, modelPath);

		Stream dataStream;
		// Read data from the streaming assets path. You cannot access the streaming assets directly on Android.
		if (dataPath.Contains("://"))
		{
			UnityWebRequest www = UnityWebRequest.Get(dataPath);
			www.SendWebRequest();
			while (!www.isDone)
			{
				yield return null;
			}
			dataStream = new MemoryStream(www.downloadHandler.data);
		}
		// Read the file directly on valid platforms.
		else
		{
			dataStream = File.OpenRead(dataPath);
		}

		//Read the Zip File
		var zipFile = ZipFile.Read(dataStream);

		//Start Extraction
		zipFile.ExtractAll(Application.persistentDataPath);

		//Set the decompressed path
		setPath(decompressedPath);

		//Wait a second in case we need to initialize another object.
		yield return new WaitForSeconds(0.5f);
		//Dispose the zipfile reader.
		zipFile.Dispose();
		dataStream.Dispose();
	}

	//Callback from the voice processor when new audio is detected
	private void VoiceProcessorOnFrameCaptured(short[] samples)
	{	
                _threadedBufferQueue.Enqueue(samples);
	}
	
	/// <summary>
	/// Check if Vosk is fully initialized and ready to process speech
	/// </summary>
	public bool IsInitialized()
	{
		return _didInit && _chineseModel != null && _englishModel != null;
	}

	/// <summary>
	/// Check if Vosk is currently processing (initializing or running)
	/// </summary>
	public bool IsProcessing()
	{
		return _isInitializing || _running;
	}
}
