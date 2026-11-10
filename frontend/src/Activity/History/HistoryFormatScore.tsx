import React from 'react';
import Tooltip from 'Components/Tooltip/Tooltip';
import { tooltipPositions } from 'Helpers/Props';
import MovieFormats from 'Movie/MovieFormats';
import CustomFormat from 'typings/CustomFormat';
import formatCustomFormatScore from 'Utilities/Number/formatCustomFormatScore';
import translate from 'Utilities/String/translate';

interface HistoryFormatScoreProps {
  score?: number;
  formats: CustomFormat[];
  error?: string;
}

function HistoryFormatScore({
  score,
  formats,
  error,
}: HistoryFormatScoreProps) {
  return (
    <Tooltip
      anchor={
        score == null
          ? translate('Unknown')
          : formatCustomFormatScore(score, formats.length)
      }
      tooltip={score == null ? error : <MovieFormats formats={formats} />}
      position={tooltipPositions.BOTTOM}
    />
  );
}

export default HistoryFormatScore;
