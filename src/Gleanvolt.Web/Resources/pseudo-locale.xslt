<?xml version="1.0" encoding="utf-8"?>
<!--
  Turns a neutral .resx into the pseudo-locale (see GeneratePseudoLocale in Gleanvolt.Web.csproj).
  Only letters are swapped, so {0} placeholders survive. Keys ending in "Pattern" or "Template" are date
  and time formats and are copied untouched: "yyyy" accented is no longer a format.
-->
<xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
  <xsl:template match="@*|node()">
    <xsl:copy>
      <xsl:apply-templates select="@*|node()" />
    </xsl:copy>
  </xsl:template>

  <xsl:template match="data[not(substring(@name, string-length(@name) - 6) = 'Pattern') and not(substring(@name, string-length(@name) - 7) = 'Template')]/value/text()">
    <xsl:text>[</xsl:text>
    <xsl:value-of select="translate(., 'aceinorsuzACEINORSUZ', 'áčéíñöřšúžÁČÉÍÑÖŘŠÚŽ')" />
    <xsl:text>]</xsl:text>
  </xsl:template>
</xsl:stylesheet>
