from django import forms

# A multi-day Garmin track stays well under this; anything larger is a mistake.
MAX_GPX_UPLOAD_SIZE = 25 * 1024 * 1024


class TripEditForm(forms.Form):
    """Form for editing trip details"""
    # UIAA climbing grade choices
    UIAA_GRADE_CHOICES = [
        ('', '-- Select Grade --'),
        ('I', 'I'),
        ('II', 'II'),
        ('III', 'III'),
        ('III+', 'III+'),
        ('IV-', 'IV-'),
        ('IV', 'IV'),
        ('IV+', 'IV+'),
        ('V-', 'V-'),
        ('V', 'V'),
        ('V+', 'V+'),
        ('VI-', 'VI-'),
        ('VI', 'VI'),
        ('VI+', 'VI+'),
        ('VII-', 'VII-'),
        ('VII', 'VII'),
        ('VII+', 'VII+'),
        ('VIII-', 'VIII-'),
        ('VIII', 'VIII'),
        ('VIII+', 'VIII+'),
        ('IX-', 'IX-'),
        ('IX', 'IX'),
        ('IX+', 'IX+'),
        ('X-', 'X-'),
        ('X', 'X'),
        ('X+', 'X+'),
        ('XI-', 'XI-'),
        ('XI', 'XI'),
        ('XI+', 'XI+'),
        ('XII-', 'XII-'),
        ('XII', 'XII'),
    ]
    
    # Alpine grade choices
    ALPINE_GRADE_CHOICES = [
        ('', '-- Select Grade --'),
        ('F', 'F (Facile/Easy)'),
        ('F+', 'F+ (Facile+/Easy+)'),
        ('PD-', 'PD- (Peu Difficile-/Slightly Difficult-)'),
        ('PD', 'PD (Peu Difficile/Slightly Difficult)'),
        ('PD+', 'PD+ (Peu Difficile+/Slightly Difficult+)'),
        ('AD-', 'AD- (Assez Difficile-/Fairly Difficult-)'),
        ('AD', 'AD (Assez Difficile/Fairly Difficult)'),
        ('AD+', 'AD+ (Assez Difficile+/Fairly Difficult+)'),
        ('D-', 'D- (Difficile-/Difficult-)'),
        ('D', 'D (Difficile/Difficult)'),
        ('D+', 'D+ (Difficile+/Difficult+)'),
        ('TD-', 'TD- (Très Difficile-/Very Difficult-)'),
        ('TD', 'TD (Très Difficile/Very Difficult)'),
        ('TD+', 'TD+ (Très Difficile+/Very Difficult+)'),
        ('ED1', 'ED1 (Extrêmement Difficile 1/Extremely Difficult 1)'),
        ('ED2', 'ED2 (Extrêmement Difficile 2/Extremely Difficult 2)'),
        ('ED3', 'ED3 (Extrêmement Difficile 3/Extremely Difficult 3)'),
        ('ED4', 'ED4 (Extrêmement Difficile 4/Extremely Difficult 4)'),
        ('ED5', 'ED5 (Extrêmement Difficile 5/Extremely Difficult 5)'),
        ('ABO', 'ABO (Abominably Difficult)'),
    ]
    
    # Trip class choices
    TRIP_CLASS_CHOICES = [
        ('', '--Vyber kategorii--'),
        ('none', 'none'),
        ('Mountaineering', 'VHT'),
        ('Skialp', 'Skialpy'),
        ('Slope', 'Sjezdovka'),
        ('Climb', 'Lezení'),
        ('Run', 'Běh'),
        ('Ferratta', 'Via ferrata'),
        ('Trail', 'Trek'),
    ]
    
    # Ferrata grade choices
    FERRATA_GRADE_CHOICES = [
        ('', '-- Select Grade --'),
        ('A', 'A (Easy)'),
        ('B', 'B (Moderate)'),
        ('C', 'C (Difficult)'),
        ('D', 'D (Very Difficult)'),
        ('E', 'E (Extremely Difficult)'),
        ('F', 'F (Exceptionally Difficult)'),
    ]
    
    title = forms.CharField(max_length=200, required=True, 
                           widget=forms.TextInput(attrs={'class': 'form-control'}))
    description = forms.CharField(required=False, 
                                 widget=forms.Textarea(attrs={'class': 'form-control', 'rows': 5}))
    trip_completed_on = forms.DateField(required=False,
                                       widget=forms.DateInput(attrs={'class': 'form-control', 'type': 'date'}))
    length_hours = forms.FloatField(required=False, min_value=0,
                                   widget=forms.NumberInput(attrs={'class': 'form-control', 'step': '0.5'}))
    participants = forms.CharField(required=False,
                                  widget=forms.Textarea(attrs={'class': 'form-control', 'rows': 3}))
    meters_ascend = forms.IntegerField(required=False, min_value=0,
                                      widget=forms.NumberInput(attrs={'class': 'form-control'}))
    meters_descend = forms.IntegerField(required=False, min_value=0,
                                       widget=forms.NumberInput(attrs={'class': 'form-control'}))
    
    # Climbing and hiking grade fields as select dropdowns
    uiaa_grade = forms.ChoiceField(choices=UIAA_GRADE_CHOICES, required=False,
                                  widget=forms.Select(attrs={'class': 'form-control'}))
    alpine_grade = forms.ChoiceField(choices=ALPINE_GRADE_CHOICES, required=False,
                                    widget=forms.Select(attrs={'class': 'form-control'}))
    trip_class = forms.ChoiceField(choices=TRIP_CLASS_CHOICES, required=False,
                                  widget=forms.Select(attrs={'class': 'form-control'}))
    ferata_grade = forms.ChoiceField(choices=FERRATA_GRADE_CHOICES, required=False,
                                    widget=forms.Select(attrs={'class': 'form-control'}))
    
    # Photo upload field
    photos = forms.FileField(required=False, 
                            widget=forms.FileInput(attrs={
                                'class': 'form-control',
                                'accept': 'image/*'
                            }))
    
    # GPX track upload - a Garmin Connect export, or any other GPX file
    gpx_file = forms.FileField(required=False,
                              widget=forms.FileInput(attrs={
                                  'class': 'form-control',
                                  'accept': '.gpx,application/gpx+xml'
                              }))
    gpx_autofill = forms.BooleanField(required=False, initial=True,
                                     widget=forms.CheckboxInput(attrs={'class': 'form-check-input'}))

    # Location fields (hidden)
    parking_json = forms.CharField(required=False, widget=forms.HiddenInput())
    high_point_json = forms.CharField(required=False, widget=forms.HiddenInput())

    def clean_gpx_file(self):
        """Reject files that are obviously not a usable GPX before parsing them."""
        gpx_file = self.cleaned_data.get('gpx_file')
        if not gpx_file:
            return gpx_file

        if not gpx_file.name.lower().endswith('.gpx'):
            raise forms.ValidationError(
                "Nahraj soubor ve formátu .gpx (v Garmin Connect: aktivita → ⚙ → Export to GPX)."
            )

        if gpx_file.size > MAX_GPX_UPLOAD_SIZE:
            raise forms.ValidationError(
                f"GPX soubor je příliš velký (max {MAX_GPX_UPLOAD_SIZE // (1024 * 1024)} MB)."
            )

        return gpx_file
