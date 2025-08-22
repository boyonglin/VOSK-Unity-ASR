using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Real-time 10-second recording controller with audio intensity display
/// </summary>
public class SimpleRecordingController : MonoBehaviour
{
    [Header("UI Elements")]
    public Button recordButton;
    public Text statusText;
    public Text resultText;
    public Text rawVoskResultText;
    public Text rawVoskResultText1;

    [Header("Recording Settings")]
    public float recordingDuration = 10f;

    [Header("Audio Level Settings")]
    [Range(0.001f, 0.1f)]
    public float audioThreshold = 0.01f; // Minimum level to consider as speech
    public bool showRawValues = true; // Show exact audio values

    [Header("Required Components")]
    public VoiceProcessor voiceProcessor;
    public VoskSpeechToText voskSpeechToText;

    // Private variables
    private bool isRecording = false;
    private Coroutine recordingCoroutine;
    private bool hasDetectedAudio = false;
    private float totalAudioLevel = 0f;
    private int audioSamples = 0;
    private string currentRecognitionResult = "";
    private string accumulatedText = ""; // Store all winning results regardless of language
    private zhSimplify2Traditional chineseConverter; // Chinese converter instance

    // Audio intensity tracking
    private float currentAudioLevel = 0f;
    private float averageAudioLevel = 0f;

    void Start()
    {
        SetupUI();
        ConnectToVosk();
        InitializeChineseConverter();
    }

    void InitializeChineseConverter()
    {
        chineseConverter = new zhSimplify2Traditional();
    }

    void SetupUI()
    {
        if (recordButton != null)
        {
            recordButton.onClick.AddListener(ToggleRecording);
            UpdateButtonText();
        }

        if (statusText != null)
            statusText.text = "Ready to record";

        if (resultText != null)
            resultText.text = "Real-time speech recognition will appear here...";
    }

    void ConnectToVosk()
    {
        if (voskSpeechToText != null)
        {
            // Subscribe to transcription results - this gives us real-time results
            voskSpeechToText.OnTranscriptionResult += OnTranscriptionReceived;
            voskSpeechToText.OnStatusUpdated += OnVoskStatusUpdated;
            // Subscribe to language detection events
            voskSpeechToText.OnLanguageDetected += OnLanguageDetected;
            // Subscribe to detailed results that include both model outputs
            voskSpeechToText.OnDetailedTranscriptionResult += OnDetailedTranscriptionReceived;
        }

        if (voiceProcessor != null)
        {
            // Subscribe to audio events
            voiceProcessor.OnFrameCaptured += OnAudioFrameReceived;
        }
    }

    public void ToggleRecording()
    {
        if (isRecording)
        {
            StopRecording();
        }
        else
        {
            StartRecording();
        }
    }

    public void StartRecording()
    {
        if (isRecording) return;

        if (voiceProcessor == null || voskSpeechToText == null)
        {
            Debug.LogError("VoiceProcessor or VoskSpeechToText not assigned!");
            if (statusText != null)
                statusText.text = "Error: Missing components";
            return;
        }

        // Check if Vosk is properly initialized before starting
        if (!IsVoskReady())
        {
            if (statusText != null)
                statusText.text = "Waiting for Vosk to initialize...";
            Debug.LogWarning("Vosk not ready yet, delaying start...");
            StartCoroutine(WaitForVoskAndStart());
            return;
        }

        // Reset detection variables
        hasDetectedAudio = false;
        totalAudioLevel = 0f;
        audioSamples = 0;
        currentRecognitionResult = "";
        currentAudioLevel = 0f;
        averageAudioLevel = 0f;
        accumulatedText = ""; // Reset accumulated text

        isRecording = true;

        // Start recording coroutine
        recordingCoroutine = StartCoroutine(RecordingTimer());

        // IMPORTANT: Start Vosk speech recognition thread safely (only if Vosk is ready)
        if (voskSpeechToText != null && IsVoskReady())
        {
            // Start only the speech recognition processing thread
            voskSpeechToText.StartSpeechProcessing();
        }

        // Start voice processor with real-time processing  
        voiceProcessor.StartRecording(16000, 512, false);

        // Update UI
        UpdateButtonText();
        if (statusText != null)
            statusText.text = "Recording... Speak now!";
        if (resultText != null)
            resultText.text = "Listening...";
    }

    public void StopRecording()
    {
        if (!isRecording) return;

        isRecording = false;

        // Stop the recording coroutine
        if (recordingCoroutine != null)
        {
            StopCoroutine(recordingCoroutine);
            recordingCoroutine = null;
        }

        // Stop Vosk speech processing thread safely
        if (voskSpeechToText != null)
        {
            voskSpeechToText.StopSpeechProcessing();
        }

        // Stop voice processor
        if (voiceProcessor != null && voiceProcessor.IsRecording)
        {
            voiceProcessor.StopRecording();
        }

        // Update UI
        UpdateButtonText();

        // Show final status with audio statistics
        string audioStats = hasDetectedAudio ? 
            $"Audio detected! Avg: {averageAudioLevel:F4}" : 
            "No audio detected - check microphone";
        
        if (statusText != null)
            statusText.text = audioStats;

        // Keep the final result displayed
        if (string.IsNullOrEmpty(currentRecognitionResult) && !hasDetectedAudio)
        {
            if (resultText != null)
                resultText.text = "No speech detected - try speaking louder";
        }

        Debug.Log($"Recording stopped. Audio detected: {hasDetectedAudio}");
    }

    IEnumerator RecordingTimer()
    {
        float remainingTime = recordingDuration;
        
        while (remainingTime > 0 && isRecording)
        {
            // Update status with remaining time and current audio level
            if (statusText != null)
            {
                string audioStatus = hasDetectedAudio ? "✓ Audio OK" : "⚠ Low audio";
                statusText.text = $"Recording... {remainingTime:F0}s ({audioStatus})";
            }
            
            yield return new WaitForSeconds(0.1f);
            remainingTime -= 0.1f;
        }

        // Auto-stop after 10 seconds
        if (isRecording)
        {
            StopRecording();
        }
    }

    void UpdateButtonText()
    {
        if (recordButton != null)
        {
            Text buttonText = recordButton.GetComponentInChildren<Text>();
            if (buttonText != null)
            {
                buttonText.text = isRecording ? "Stop Recording" : "Start Audio Test";
            }
        }
    }

    // Event handlers
    void OnAudioFrameReceived(short[] audioData)
    {
        if (!isRecording) return;

        // Calculate audio level intensity
        float sum = 0f;
        float maxSample = 0f;
        
        for (int i = 0; i < audioData.Length; i++)
        {
            float sample = Mathf.Abs(audioData[i]) / (float)short.MaxValue;
            sum += sample;
            if (sample > maxSample)
                maxSample = sample;
        }
        
        currentAudioLevel = sum / audioData.Length;
        
        // Track statistics
        totalAudioLevel += currentAudioLevel;
        audioSamples++;
        averageAudioLevel = totalAudioLevel / audioSamples;
        
        // Consider audio detected if level is above threshold
        if (currentAudioLevel > audioThreshold)
        {
            hasDetectedAudio = true;
        }
    }
    
    void OnTranscriptionReceived(string transcription)
    {
        // This method is now DISABLED in favor of OnDetailedTranscriptionReceived
        // which provides proper dual-model competition results
        
        // We only log the raw result but don't accumulate text here anymore
        Debug.Log($"Raw Vosk Result (Legacy): {transcription}");
        
        // All text accumulation now happens in OnDetailedTranscriptionReceived
        // which properly handles winner-only accumulation
    }

    // Convert Chinese text from simplified to traditional if enabled
    private string ConvertChineseText(string originalText)
    {
        if (chineseConverter != null)
        {
            try
            {
                string convertedText = chineseConverter.convert(originalText);
                return convertedText;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Error converting Chinese text: {ex.Message}");
                return originalText; // Return original text if conversion fails
            }
        }
        
        return originalText; // Return original text if conversion is disabled
    }

    // Extract actual text from Vosk JSON result
    private string ExtractTextFromVoskResult(string jsonResult)
    {
        try
        {
            if (string.IsNullOrEmpty(jsonResult))
                return "";

            // Parse the JSON to extract the text
            if (jsonResult.Contains("\"text\""))
            {
                // Simple JSON parsing to extract text field
                int textStart = jsonResult.IndexOf("\"text\" : \"") + 10;
                if (textStart > 9)
                {
                    int textEnd = jsonResult.IndexOf("\"", textStart);
                    if (textEnd > textStart)
                    {
                        return jsonResult.Substring(textStart, textEnd - textStart).Trim();
                    }
                }
            }
            
            // If we have alternatives, try to get the first one with highest confidence
            if (jsonResult.Contains("\"alternatives\""))
            {
                // Find first text in alternatives array
                int altStart = jsonResult.IndexOf("\"text\" : \"");
                if (altStart > -1)
                {
                    altStart += 10;
                    int altEnd = jsonResult.IndexOf("\"", altStart);
                    if (altEnd > altStart)
                    {
                        return jsonResult.Substring(altStart, altEnd - altStart).Trim();
                    }
                }
            }
            
            return "";
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"Error parsing Vosk result: {ex.Message}");
            return "";
        }
    }

    void OnVoskStatusUpdated(string status)
    {
        Debug.Log($"Vosk status: {status}");
    }
    
    void OnDestroy()
    {
        // Clean up event subscriptions
        if (voskSpeechToText != null)
        {
            voskSpeechToText.OnTranscriptionResult -= OnTranscriptionReceived;
            voskSpeechToText.OnStatusUpdated -= OnVoskStatusUpdated;
            voskSpeechToText.OnLanguageDetected -= OnLanguageDetected;
            voskSpeechToText.OnDetailedTranscriptionResult -= OnDetailedTranscriptionReceived;
        }

        if (voiceProcessor != null)
        {
            voiceProcessor.OnFrameCaptured -= OnAudioFrameReceived;
        }

        if (isRecording)
        {
            StopRecording();
        }
    }

    // Check if Vosk is ready (initialized and not processing)
    private bool IsVoskReady()
    {
        return voskSpeechToText != null && voskSpeechToText.IsInitialized() && !voskSpeechToText.IsProcessing();
    }

    // Coroutine to wait for Vosk initialization
    private IEnumerator WaitForVoskAndStart()
    {
        while (!IsVoskReady())
        {
            Debug.Log("Waiting for Vosk to initialize...");
            if (statusText != null)
                statusText.text = "Waiting for Vosk to initialize...";
            yield return new WaitForSeconds(1f);
        }

        // Once Vosk is ready, start the recording
        Debug.Log("Vosk is now ready, starting recording...");
        StartRecording();
    }

    void OnLanguageDetected(SupportedLanguage language)
    {
        Debug.Log($"Language detected: {language}");
    }

    void OnDetailedTranscriptionReceived(VoskSpeechToText.ModelResult modelResult)
    {
        // Add detailed debug logging to understand the issue
        Debug.Log($"=== DETAILED TRANSCRIPTION DEBUG ===");
        Debug.Log($"Chinese Result: {modelResult.chineseResult}");
        Debug.Log($"English Result: {modelResult.englishResult}");
        Debug.Log($"Chinese Confidence: {modelResult.chineseConfidence:F3}");
        Debug.Log($"English Confidence: {modelResult.englishConfidence:F3}");
        Debug.Log($"Detected Language (Winner): {modelResult.detectedLanguage}");
        Debug.Log($"Best Result: {modelResult.bestResult}");
        
        // Display Chinese model result in rawVoskResultText (always)
        if (rawVoskResultText != null)
        {
            rawVoskResultText.text = modelResult.chineseResult;
        }
        
        // Display English model result in rawVoskResultText1 (always)
        if (rawVoskResultText1 != null)
        {
            rawVoskResultText1.text = modelResult.englishResult;
        }
        
        // Only accumulate text from the WINNING model
        string actualText = ExtractTextFromVoskResult(modelResult.bestResult);
        Debug.Log($"Extracted Text from Best Result: '{actualText}'");
        
        // Only process if we have actual text AND it's from the winning model
        if (!string.IsNullOrEmpty(actualText))
        {
            Debug.Log($"Processing text accumulation for winner: {modelResult.detectedLanguage}");
            
            // Accumulate the result directly without language checks
            string convertedText = ConvertChineseText(actualText);
            Debug.Log($"Converting Chinese text: '{actualText}' -> '{convertedText}'");
            
            if (!string.IsNullOrEmpty(accumulatedText))
            {
                if (!accumulatedText.EndsWith(" ") && !convertedText.StartsWith(" "))
                {
                    accumulatedText += " ";
                }
            }
            accumulatedText += convertedText;
            currentRecognitionResult = accumulatedText;
            
            Debug.Log($"Accumulated text: {accumulatedText}");
            
            if (resultText != null)
            {
                // Show accumulated text with confidence scores and winner indicator
                string confidenceInfo = $"CN:{modelResult.chineseConfidence:F2} EN:{modelResult.englishConfidence:F2} [{modelResult.detectedLanguage}]";
                resultText.text = $"{confidenceInfo}\n{currentRecognitionResult}";
                resultText.color = Color.black;
            }
        }
        else 
        {
            Debug.Log($"No text extracted from best result or empty text");
            
            if (isRecording && string.IsNullOrEmpty(accumulatedText))
            {
                if (resultText != null)
                {
                    string confidenceInfo = $"CN:{modelResult.chineseConfidence:F2} EN:{modelResult.englishConfidence:F2}";
                    resultText.text = $"{confidenceInfo}\nListening...";
                    resultText.color = Color.white;
                }
            }
        }
        
        Debug.Log($"=== END DEBUG ===");
    }
}
