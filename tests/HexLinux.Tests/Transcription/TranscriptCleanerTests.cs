using HexLinux.Transcription;
using Xunit;

namespace HexLinux.Tests.Transcription;

/// <summary>
/// Whisper does not return speech alone: it annotates ambient noise and
/// sometimes invents closing-credit boilerplate on a silent recording. Those
/// cases are checked here, because otherwise they would end up pasted verbatim
/// into the document of the user.
///
/// The dictated samples stay in French on purpose: they exercise the
/// French-specific patterns and the French typography rules, which is exactly
/// what the cleaner exists for.
/// </summary>
public class TranscriptCleanerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t  ")]
    public void An_empty_input_produces_nothing(string? input)
    {
        Assert.Equal(string.Empty, TranscriptCleaner.Clean(input));
    }

    [Fact]
    public void The_segments_are_joined_in_order()
    {
        string[] segments = [" Bonjour,", " ceci est", " un test."];

        Assert.Equal("Bonjour, ceci est un test.", TranscriptCleaner.Clean(segments));
    }

    [Fact]
    public void A_null_segment_list_produces_nothing()
    {
        Assert.Equal(string.Empty, TranscriptCleaner.Clean((IEnumerable<string?>?)null));
    }

    // --- Bracketed annotations -------------------------------------------------

    [Theory]
    [InlineData("[BLANK_AUDIO]")]
    [InlineData("[Musique]")]
    [InlineData("[APPLAUSE]")]
    [InlineData("[_BEG_]")]
    [InlineData("[bruit de fond]")]
    public void Bracketed_annotations_disappear(string annotation)
    {
        Assert.Equal("Bonjour.", TranscriptCleaner.Clean($"{annotation} Bonjour."));
    }

    [Fact]
    public void Several_annotations_disappear_together()
    {
        string cleaned = TranscriptCleaner.Clean("[Musique] Bonjour [BLANK_AUDIO] tout le monde. [Fin]");

        Assert.Equal("Bonjour tout le monde.", cleaned);
    }

    [Fact]
    public void A_text_reduced_to_annotations_produces_nothing()
    {
        // A very common case: the key is released before anything was said.
        Assert.Equal(string.Empty, TranscriptCleaner.Clean("[BLANK_AUDIO]"));
        Assert.Equal(string.Empty, TranscriptCleaner.Clean(" [Musique] [BLANK_AUDIO] "));
    }

    // --- Parenthesised annotations ---------------------------------------------

    [Theory]
    [InlineData("(Musique)")]
    [InlineData("(musique douce)")]
    [InlineData("(Applaudissements)")]
    [InlineData("(rires)")]
    [InlineData("(silence)")]
    [InlineData("(inaudible)")]
    public void Parenthesised_noises_disappear(string annotation)
    {
        Assert.Equal("Bonjour.", TranscriptCleaner.Clean($"{annotation} Bonjour."));
    }

    [Fact]
    public void A_parenthesis_that_is_part_of_the_speech_is_kept()
    {
        // An important point: not every parenthesis can be removed. They are
        // part of the dictation as soon as they do not name a noise.
        const string dictated = "Le rapport (version deux) doit partir demain.";

        Assert.Equal(dictated, TranscriptCleaner.Clean(dictated));
    }

    // --- Invented boilerplate ---------------------------------------------------

    [Theory]
    [InlineData("Sous-titres réalisés par la communauté d'Amara.org")]
    [InlineData("Sous-titrage Société Radio-Canada")]
    [InlineData("Merci d'avoir regardé cette vidéo !")]
    [InlineData("Abonnez-vous !")]
    [InlineData("Thanks for watching!")]
    public void Invented_closing_credits_disappear(string hallucination)
    {
        // Whisper was trained on video subtitles: on a near-silent recording it
        // spits these credits back out, though none of it was ever spoken.
        Assert.Equal(string.Empty, TranscriptCleaner.Clean(hallucination));
    }

    [Theory]
    [InlineData("Ajoute des sous-titres à la vidéo.")]
    [InlineData("Le sous-titrage est prêt.")]
    [InlineData("Merci d'avoir relu le document.")]
    public void A_legitimate_dictation_mentioning_subtitles_is_kept(string dictated)
    {
        // A guard against an over-eager filter: the word alone must not be
        // enough to make the sentence vanish. An attribution marker is needed
        // — "réalisés par", "Société"... — to conclude it is invented credits.
        Assert.Equal(dictated, TranscriptCleaner.Clean(dictated));
    }

    [Fact]
    public void Invented_boilerplate_at_the_end_is_removed_without_touching_the_rest()
    {
        string cleaned = TranscriptCleaner.Clean(
            "Rappelle-moi d'appeler le client demain. Merci d'avoir regardé cette vidéo !");

        Assert.Equal("Rappelle-moi d'appeler le client demain.", cleaned);
    }

    // --- Formatting -------------------------------------------------------------

    [Fact]
    public void Multiple_spaces_are_reduced_to_one()
    {
        Assert.Equal("Bonjour tout le monde.", TranscriptCleaner.Clean("Bonjour    tout\n\nle\tmonde."));
    }

    [Fact]
    public void The_leading_space_of_whisper_segments_is_removed()
    {
        // Whisper always prefixes its segments with a space.
        Assert.Equal("Bonjour.", TranscriptCleaner.Clean(" Bonjour."));
    }

    [Fact]
    public void Musical_notes_disappear()
    {
        Assert.Equal(string.Empty, TranscriptCleaner.Clean("♪ ♫"));
        Assert.Equal("Bonjour.", TranscriptCleaner.Clean("♪ Bonjour. ♪"));
    }

    // --- French typography -------------------------------------------------------

    [Theory]
    [InlineData("Tu viens vendredi?", "Tu viens vendredi ?")]
    [InlineData("Quelle horreur!", "Quelle horreur !")]
    [InlineData("Voici la liste:", "Voici la liste :")]
    [InlineData("Il part; elle reste.", "Il part ; elle reste.")]
    public void The_space_before_double_punctuation_is_restored(string raw, string expected)
    {
        // Parakeet writes "vendredi?" the English way. French usage puts a
        // space before double punctuation marks.
        Assert.Equal(expected, TranscriptCleaner.Clean(raw));
    }

    [Theory]
    [InlineData("Rendez-vous à 14:30.")]
    [InlineData("Va sur https://exemple.fr aujourd'hui.")]
    [InlineData("Le ratio est de 3:1 environ.")]
    public void A_colon_against_a_digit_or_a_url_is_left_alone(string dictated)
    {
        // The mark should only be spaced when it ends a word. Without that
        // condition, a time or an address would end up cut in two.
        Assert.Equal(dictated, TranscriptCleaner.Clean(dictated));
    }

    [Fact]
    public void A_space_already_present_is_not_doubled()
    {
        Assert.Equal("Tu viens vendredi ?", TranscriptCleaner.Clean("Tu viens vendredi ?"));
    }

    [Fact]
    public void A_text_with_no_letter_or_digit_produces_nothing()
    {
        // Inserting a lone "." or "..." would be worse than doing nothing.
        Assert.Equal(string.Empty, TranscriptCleaner.Clean("."));
        Assert.Equal(string.Empty, TranscriptCleaner.Clean(" ... "));
        Assert.Equal(string.Empty, TranscriptCleaner.Clean("!?"));
    }

    [Fact]
    public void A_normal_text_passes_through_the_cleaning_unharmed()
    {
        const string dictated =
            "Bonjour Marie, peux-tu relire le devis numéro 4218 avant vendredi ? Merci beaucoup.";

        Assert.Equal(dictated, TranscriptCleaner.Clean(dictated));
    }

    [Fact]
    public void Accents_and_apostrophes_are_preserved()
    {
        const string dictated = "L'équipe a déjà terminé l'intégration côté serveur.";

        Assert.Equal(dictated, TranscriptCleaner.Clean(dictated));
    }

    // --- The frenchSpacing switch -------------------------------------------------

    [Theory]
    [InlineData("Are you ready?")]
    [InlineData("Watch out!")]
    [InlineData("Here is the list: eggs, milk.")]
    [InlineData("He left; she stayed.")]
    [InlineData("Tu viens vendredi?")]
    public void With_french_spacing_off_the_punctuation_stays_as_the_engine_wrote_it(string dictated)
    {
        // Off means never, French text included: for whoever wants the
        // engine's punctuation untouched.
        Assert.Equal(dictated, TranscriptCleaner.Clean(dictated, frenchSpacing: false));
    }

    [Theory]
    [InlineData("Are you ready?")]
    [InlineData("Are you coming on Friday?")]
    [InlineData("What a mess!")]
    [InlineData("Here is the list: eggs, milk.")]
    [InlineData("Really?")]
    public void With_french_spacing_on_an_english_dictation_keeps_its_own_typography(string dictated)
    {
        // The engine is multilingual: "Friday ?" would be a typo in English.
        // Before HexWin's pull request #68 the rule applied to every language,
        // and the only way out was turning the switch off.
        Assert.Equal(dictated, TranscriptCleaner.Clean(dictated, frenchSpacing: true));
    }

    [Theory]
    [InlineData("Vendredi?", "Vendredi ?")]
    [InlineData("Déjà?", "Déjà ?")]
    [InlineData("OK?", "OK ?")]
    public void A_sentence_with_no_clue_to_its_language_is_treated_as_french(string raw, string expected)
    {
        // A tie counts as French: it is what the spacing always assumed.
        Assert.Equal(expected, TranscriptCleaner.Clean(raw));
    }

    [Theory]
    [InlineData("Tu viens avec you?", "Tu viens avec you ?")]
    [InlineData("I think que c'est bien!", "I think que c'est bien !")]
    [InlineData("Is it the café?", "Is it the café?")]
    public void A_mixed_sentence_goes_with_the_language_that_has_more_clues(string raw, string expected)
    {
        // "tu", "avec" against "you"; "que" against "i"; "is", "it", "the"
        // against one accent: people slip words of the other language in, and
        // the majority decides.
        Assert.Equal(expected, TranscriptCleaner.Clean(raw));
    }

    [Fact]
    public void French_spacing_is_on_when_nothing_is_said()
    {
        // HexWin always spaced; a caller that forgets the argument must keep
        // that behaviour, as settings.json's default does.
        Assert.Equal("Tu viens ?", TranscriptCleaner.Clean("Tu viens?"));
        Assert.Equal("Tu viens ?", TranscriptCleaner.Clean(["Tu", "viens?"]));
    }

    [Fact]
    public void The_switch_also_applies_to_the_segments()
    {
        // The segment overload joins first and judges the language on the
        // whole: turning the spacing off must hold there too.
        string[] segments = ["Tu es", "prêt?"];

        Assert.Equal("Tu es prêt?", TranscriptCleaner.Clean(segments, frenchSpacing: false));
        Assert.Equal("Tu es prêt ?", TranscriptCleaner.Clean(segments, frenchSpacing: true));
        Assert.Equal("Are you ready?", TranscriptCleaner.Clean(["Are you", "ready?"], frenchSpacing: true));
    }

    [Fact]
    public void The_closing_guillemet_gets_its_space()
    {
        // The engine writes the opening guillemet with its space and glues
        // the closing one to the word.
        Assert.Equal("Il a répondu « oui »", TranscriptCleaner.Clean("Il a répondu « oui»"));
        Assert.Equal("Il a répondu « oui»", TranscriptCleaner.Clean("Il a répondu « oui»", frenchSpacing: false));
    }

    [Fact]
    public void A_mark_after_a_digit_is_spaced_too()
    {
        // "Il en reste 3!" is a sentence end like any other.
        Assert.Equal("Il en reste 3 !", TranscriptCleaner.Clean("Il en reste 3!"));
    }

    [Fact]
    public void Several_marks_in_one_text_are_all_spaced()
    {
        Assert.Equal(
            "Attention : il pleut ! Tu viens ?",
            TranscriptCleaner.Clean("Attention: il pleut! Tu viens?"));
    }

    [Theory]
    [InlineData("Rendez-vous à 14:30.")]
    [InlineData("Va sur https://exemple.fr aujourd'hui.")]
    [InlineData("Le ratio est de 3:1 environ.")]
    public void Times_and_addresses_are_untouched_whichever_the_switch(string dictated)
    {
        Assert.Equal(dictated, TranscriptCleaner.Clean(dictated, frenchSpacing: true));
        Assert.Equal(dictated, TranscriptCleaner.Clean(dictated, frenchSpacing: false));
    }

    [Fact]
    public void Turning_the_spacing_off_does_not_turn_the_cleaning_off()
    {
        // The switch is about typography only: the noise annotations and the
        // invented credits must go either way.
        Assert.Equal("Are you ready?", TranscriptCleaner.Clean("[Music] Are you ready? Thanks for watching!", frenchSpacing: false));
    }

    // --- Every noise the parenthesis filter knows ---------------------------------

    [Theory]
    [InlineData("(Music)")]
    [InlineData("(musiques)")]
    [InlineData("(applause)")]
    [InlineData("(applaudissement)")]
    [InlineData("(Laughter)")]
    [InlineData("(rire)")]
    [InlineData("(bruit)")]
    [InlineData("(bruits de pas)")]
    [InlineData("(soupir)")]
    [InlineData("(soupirs)")]
    [InlineData("(toux)")]
    [InlineData("(sifflement)")]
    [InlineData("(sifflements)")]
    [InlineData("( MUSIC )")]
    public void Every_noise_named_in_parentheses_disappears(string annotation)
    {
        // The list is data the model emits, in French and in English, in any
        // case: each entry is checked, since a typo in one would let it be
        // pasted into the user's document.
        Assert.Equal("Bonjour.", TranscriptCleaner.Clean($"{annotation} Bonjour."));
    }

    [Theory]
    [InlineData("Le groupe (musiciens compris) arrive demain.")]
    [InlineData("Le film (bruitage maison) sort demain.")]
    public void A_parenthesis_starting_with_a_longer_word_is_kept(string dictated)
    {
        // "musiciens" begins with "music", "bruitage" with "bruit", but they
        // name people and a craft, not a noise: the word boundary keeps the
        // dictated parenthesis.
        Assert.Equal(dictated, TranscriptCleaner.Clean(dictated));
    }

    [Theory]
    [InlineData("Écris [BLANK puis la suite.")]
    [InlineData("Note (musique du film à choisir.")]
    public void An_annotation_that_is_never_closed_removes_nothing(string dictated)
    {
        // An opening bracket or parenthesis the engine never closed is not an
        // annotation: removing from it to the end would swallow everything
        // said after it.
        Assert.Equal(dictated, TranscriptCleaner.Clean(dictated));
    }

    // --- Every invented credit ------------------------------------------------------

    [Theory]
    [InlineData("Sous-titres par Jean Dupont.")]
    [InlineData("Sous-titrage par l'équipe")]
    [InlineData("Sous-titres réalisé par la communauté")]
    [InlineData("Sous-titrage Societe Radio-Canada")]
    [InlineData("Sous-titrage ST' 501")]
    [InlineData("Sous-titres MFP.")]
    [InlineData("SousTitreur.com")]
    [InlineData("Amara.org")]
    [InlineData("Merci d'avoir regarde cette video")]
    [InlineData("ABONNEZ-VOUS")]
    [InlineData("Thanks for watching")]
    [InlineData("Subtitles by the Amara.org community")]
    [InlineData("Subtitles by John Smith.")]
    public void Every_form_of_invented_credits_disappears(string hallucination)
    {
        // With or without accents, in any case: Whisper reproduces these as
        // it saw them in subtitles, and nobody ever dictates them.
        Assert.Equal(string.Empty, TranscriptCleaner.Clean(hallucination));
    }

    [Fact]
    public void Invented_credits_stop_at_the_end_of_their_sentence()
    {
        // What follows the credits' full stop was spoken: it must survive.
        Assert.Equal("Bonjour Marie.", TranscriptCleaner.Clean("Sous-titres par Jean Dupont. Bonjour Marie."));
    }

    [Fact]
    public void Musical_symbols_of_every_kind_disappear()
    {
        Assert.Equal("Bonjour.", TranscriptCleaner.Clean("🎵 Bonjour. 🎶"));
        Assert.Equal(string.Empty, TranscriptCleaner.Clean("♪♫🎵🎶"));
    }

    [Fact]
    public void Empty_and_null_segments_are_skipped()
    {
        // The engine returns an empty string for a segment of silence: it
        // must not leave a double space in the joined text.
        string?[] segments = ["Bonjour", null, "", "Marie."];

        Assert.Equal("Bonjour Marie.", TranscriptCleaner.Clean(segments));
    }
}
