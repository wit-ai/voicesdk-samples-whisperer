/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * This source code is licensed under the license found in the
 * LICENSE file in the root directory of this source tree.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Facebook.WitAi.Data.Configuration;
using Facebook.WitAi.Lib;
using Facebook.WitAi.TTS.Integrations;
using Facebook.WitAi.TTS.Utilities;
using UnityEngine;

namespace Facebook.WitAi.TTS.Editor.Voices
{
    public static class TTSWitVoiceUtility
    {
        private static TTSWitVoiceData[] _voices;
        private static List<string> _voiceNames;

        // Init gui
        private static bool _isGuiInit;

        // Wit voice data
        public static TTSWitVoiceData[] Voices
        {
            get
            {
                if (_voices == null) LoadVoices();
                return _voices;
            }
        }

        // Wit voice ids
        public static List<string> VoiceNames
        {
            get
            {
                if (_voiceNames == null) LoadVoices();
                return _voiceNames;
            }
        }

        // Wit voices are loading
        public static bool IsLoading { get; private set; }

        // Wit voices are updating
        public static bool IsUpdating { get; private set; }

        // Log
        private static void Log(string comment, bool error = false)
        {
            var final = "TTS Wit Voice Utility - " + comment;
            if (error) Debug.LogError(final);
        }

        #region LOAD

        // Persistent cache file path for getting voices without network
        public static string GetVoiceFilePath()
        {
            return Application.dataPath.Replace("/Assets", "/ProjectSettings") + "/wit_voices.json";
        }

        // Load voices from disk
        public static void LoadVoices(Action<bool> onComplete = null)
        {
            // Add service GUI
            if (!_isGuiInit)
            {
                _isGuiInit = true;
                TTSServiceInspector.onAdditionalGUI += OnServiceGUI;
            }

            // Already loading/updating
            if (IsLoading || IsUpdating)
            {
                onComplete?.Invoke(false);
                return;
            }

            // Voice from disk missing
            var backupPath = GetVoiceFilePath();
            if (!File.Exists(backupPath))
            {
                onComplete?.Invoke(false);
                return;
            }

            // Loading
            IsLoading = true;

            // Load file
            var json = string.Empty;
            try
            {
                json = File.ReadAllText(backupPath);
                Log($"Load Success\n{json}");
            }
            catch (Exception e)
            {
                Log($"Load Failure\n{e}", true);
                IsLoading = false;
                onComplete?.Invoke(false);
                return;
            }

            // Decode if possible
            DecodeVoices(json, onComplete);
        }

        // Decode voices
        private static void DecodeVoices(string json, Action<bool> onComplete)
        {
            // Decode
            var response = WitResponseNode.Parse(json);
            if (response == null)
            {
                Log("Decode Failure\nCould not parse", true);
                IsLoading = false;
                onComplete?.Invoke(false);
                return;
            }

            // Get locales
            var localeRoot = response.AsObject;
            var locales = localeRoot.ChildNodeNames;
            if (locales == null)
            {
                Log("Decode Failure\nNo locales found", true);
                IsLoading = false;
                onComplete?.Invoke(false);
            }

            // Iterate locales
            var voiceList = new List<TTSWitVoiceData>();
            foreach (var locale in locales)
            {
                var localeChildren = localeRoot[locale].AsArray;
                foreach (WitResponseNode voice in localeChildren) voiceList.Add(voice.AsTTSWitVoiceData());
            }

            // Finish
            OnDecodeComplete(voiceList.ToArray(), onComplete);
        }

        // Cast to voice data
        public static TTSWitVoiceData AsTTSWitVoiceData(this WitResponseNode responseNode)
        {
            // Get result
            object result = new TTSWitVoiceData();
            var voiceType = typeof(TTSWitVoiceData);

            // Get root & field names
            var voiceRoot = responseNode.AsObject;
            var voiceFieldNames = voiceRoot.ChildNodeNames;
            foreach (var voiceFieldName in voiceFieldNames)
            {
                var field = voiceType.GetField(voiceFieldName);
                if (field != null && field.IsPublic && !field.IsStatic)
                {
                    // Get value
                    object val = null;
                    // String
                    if (field.FieldType == typeof(string))
                        val = voiceRoot[voiceFieldName].Value;
                    // String[]
                    else if (field.FieldType == typeof(string[])) val = voiceRoot[voiceFieldName].AsStringArray;
                    // Set value
                    if (val != null) field.SetValue(result, val);
                }
                else
                {
                    Log($"Decode Warning\nUnknown field: {voiceFieldName}", true);
                }
            }

            // Return result
            return (TTSWitVoiceData)result;
        }

        // On decode complete
        private static void OnDecodeComplete(TTSWitVoiceData[] newVoices, Action<bool> onComplete)
        {
            // Decode failed
            if (newVoices == null || newVoices.Length == 0)
            {
                Log("Decode Failure", true);
                IsLoading = false;
                onComplete?.Invoke(false);
                return;
            }

            // Apply voices & names
            _voices = newVoices;
            _voiceNames = new List<string>();
            var voiceLog = new StringBuilder();
            foreach (var voice in _voices)
            {
                _voiceNames.Add(voice.name);
                voiceLog.Append($"\n{voice.name}");
                voiceLog.Append($"\n\tLocale: {voice.locale}");
                voiceLog.Append($"\n\tGender: {voice.gender}");
                voiceLog.Append($"\n\tStyles: {voice.styles.Length}");
            }

            // Success
            Log($"Decode Success{voiceLog}");

            // Complete
            IsLoading = false;
            onComplete?.Invoke(true);
        }

        #endregion

        #region UPDATE

        // Obtain voices
        public static void UpdateVoices(WitConfiguration configuration, Action<bool> onComplete)
        {
            // Ignore if already updating
            if (IsUpdating || IsLoading)
            {
                onComplete?.Invoke(false);
                return;
            }

            // Begin update
            IsUpdating = true;

            // Download
            Log("Service Download Begin");
            WitUnityRequest.RequestTTSVoices(configuration, null, (json, error) =>
            {
                // Failed
                if (!string.IsNullOrEmpty(error))
                {
                    Log($"Service Download Failure\n{error}", true);
                    OnUpdateComplete(false, onComplete);
                    return;
                }

                // Success
                Log($"Service Download Success\n{json}");

                // Decode if possible
                DecodeVoices(json, success =>
                {
                    // Decoded successfully, then save
                    if (success)
                    {
                        var backupPath = GetVoiceFilePath();
                        try
                        {
                            File.WriteAllText(backupPath, json);
                            Log($"Service Save Success\nPath: {backupPath}");
                        }
                        catch (Exception e)
                        {
                            Log($"Service Save Failed\nPath: {backupPath}\n{e}", true);
                        }
                    }

                    // Complete
                    OnUpdateComplete(success, onComplete);
                });
            });
        }

        // Voices decoded
        private static void OnUpdateComplete(bool success, Action<bool> onComplete)
        {
            // Stop update
            IsUpdating = false;

            // Failed & no voices, try loading
            if (!success && _voices == null)
            {
                LoadVoices(loadSuccess => onComplete?.Invoke(success));
                return;
            }

            // Invoke
            onComplete?.Invoke(success);
        }

        #endregion

        #region GUI

        // Updating GUI
        private static bool _forcedUpdate;

        private static void OnServiceGUI(TTSService service)
        {
            // Wrong type
            if (service.GetType() != typeof(TTSWit) || Application.isPlaying) return;

            // Get data
            var text = "Update Voice List";
            var canUpdate = true;
            if (IsUpdating)
            {
                text = "Updating Voice List";
                canUpdate = false;
            }
            else if (IsLoading)
            {
                text = "Loading Voice List";
                canUpdate = false;
            }

            // Layout update
            GUI.enabled = canUpdate;
            if (WitEditorUI.LayoutTextButton(text) && canUpdate)
            {
                var wit = service as TTSWit;
                UpdateVoices(wit.RequestSettings.configuration, null);
            }

            GUI.enabled = true;

            // Force an update
            if (!_forcedUpdate && canUpdate && (_voices == null || _voices.Length == 0))
            {
                _forcedUpdate = true;
                var wit = service as TTSWit;
                UpdateVoices(wit.RequestSettings.configuration, null);
            }
        }

        #endregion
    }
}
